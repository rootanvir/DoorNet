using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DoorNet.Core.Firewall;

namespace DoorNet.Capture;

/// <summary>
/// Blocks an application's outbound traffic with a Windows Defender Firewall rule.
/// <para>
/// GlassWire does this with its own WFP driver; DoorNet deliberately does not ship a driver.
/// A per-program outbound block rule is supported by every Windows version we target,
/// visible in Windows Firewall, and removable without reinstalling anything — at the cost
/// of requiring elevation and of only taking effect while Windows Firewall is enforcing.
/// </para>
/// </summary>
public sealed class WindowsFirewallController : IFirewallController
{
    private const string PolicyProgId = "HNetCfg.FwPolicy2";
    private const string RuleProgId = "HNetCfg.FwRule";

    private const int RuleDirectionOut = 2;
    private const int RuleActionBlock = 0;
    private const int ProtocolAny = 256;
    private const int ProfileAll = int.MaxValue;

    private readonly object _sync = new();
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _statePath;

    private object? _policy;
    private object? _rules;
    private bool _comUnavailable;

    public WindowsFirewallController()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoorNet");

        Directory.CreateDirectory(folder);
        _statePath = Path.Combine(folder, "firewall.json");

        LoadState();
        VerifyRulesIfPossible();
    }

    public bool IsAvailable => Environment.IsPrivilegedProcess && !_comUnavailable;

    public string? UnavailableReason
    {
        get
        {
            if (!Environment.IsPrivilegedProcess)
                return "Administrator rights are required to change firewall rules.";

            return _comUnavailable
                ? "The Windows Firewall API is unavailable on this system."
                : null;
        }
    }

    public IReadOnlyCollection<string> BlockedPaths
    {
        get
        {
            lock (_sync)
                return _blocked.ToArray();
        }
    }

    public bool IsBlocked(string? executablePath)
    {
        var normalized = Normalise(executablePath);
        if (normalized is null)
            return false;

        lock (_sync)
            return _blocked.Contains(normalized);
    }

    public FirewallChangeResult Block(string? executablePath)
    {
        var path = Normalise(executablePath);
        if (path is null)
            return FirewallChangeResult.Fail(FirewallChangeStatus.InvalidPath, "The executable path is not available for this process.");

        if (!IsAvailable)
            return FirewallChangeResult.Fail(FirewallChangeStatus.NotElevated, UnavailableReason!);

        if (!File.Exists(path))
            return FirewallChangeResult.Fail(FirewallChangeStatus.InvalidPath, "The executable no longer exists on disk.");

        lock (_sync)
        {
            try
            {
                var rules = Rules;
                var name = FirewallRuleNames.ForPath(path);

                if (!RuleExists(rules, name))
                {
                    dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID(RuleProgId)!)!;
                    rule.Name = name;
                    rule.Description = "Blocked by DoorNet";
                    rule.Grouping = FirewallRuleNames.Group;
                    rule.Direction = RuleDirectionOut;
                    rule.Action = RuleActionBlock;
                    rule.Enabled = true;
                    rule.InterfaceTypes = "All";
                    rule.Profiles = ProfileAll;
                    rule.Protocol = ProtocolAny;
                    rule.ApplicationName = path;

                    rules.Add(rule);
                }

                _blocked.Add(path);
                SaveState();
                return FirewallChangeResult.Ok($"Blocked internet access for {Path.GetFileName(path)}.");
            }
            catch (Exception ex)
            {
                return FirewallChangeResult.Fail(FirewallChangeStatus.Failed, Explain(ex));
            }
        }
    }

    public FirewallChangeResult Allow(string? executablePath)
    {
        var path = Normalise(executablePath);
        if (path is null)
            return FirewallChangeResult.Fail(FirewallChangeStatus.InvalidPath, "The executable path is not available for this process.");

        if (!IsAvailable)
            return FirewallChangeResult.Fail(FirewallChangeStatus.NotElevated, UnavailableReason!);

        lock (_sync)
        {
            try
            {
                var name = FirewallRuleNames.ForPath(path);
                if (RuleExists(Rules, name))
                    Rules.Remove(name);

                _blocked.Remove(path);
                SaveState();
                return FirewallChangeResult.Ok($"Allowed internet access for {Path.GetFileName(path)}.");
            }
            catch (Exception ex)
            {
                return FirewallChangeResult.Fail(FirewallChangeStatus.Failed, Explain(ex));
            }
        }
    }

    private dynamic Rules => _rules ??= Policy.Rules;

    private dynamic Policy
    {
        get
        {
            if (_policy is not null)
                return _policy;

            var type = Type.GetTypeFromProgID(PolicyProgId)
                       ?? throw new InvalidOperationException("Windows Firewall COM API not found.");

            _policy = Activator.CreateInstance(type)
                      ?? throw new InvalidOperationException("Windows Firewall COM API could not start.");

            return _policy;
        }
    }

    private static bool RuleExists(dynamic rules, string name)
    {
        try
        {
            _ = rules.Item(name);
            return true;
        }
            catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Drops state entries whose rule no longer exists (removed from Windows Firewall UI or
    /// by another tool). Only possible when elevated, because reading rules needs admin.
    /// </summary>
    private void VerifyRulesIfPossible()
    {
        if (!IsAvailable)
            return;

        lock (_sync)
        {
            try
            {
                var live = _blocked.Where(path => RuleExists(Rules, FirewallRuleNames.ForPath(path))).ToList();

                if (live.Count != _blocked.Count)
                {
                    _blocked.Clear();
                    foreach (var path in live)
                        _blocked.Add(path);

                    SaveState();
                }
            }
            catch (Exception)
            {
                _comUnavailable = true;
            }
        }
    }

    private static string? Normalise(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        try
        {
            var full = Path.GetFullPath(executablePath);
            return full.StartsWith(@"\\?\") ? full[4..] : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
                return;

            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_statePath));
            if (paths is null)
                return;

            foreach (var path in paths)
            {
                if (Normalise(path) is { } normalized)
                    _blocked.Add(normalized);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveState()
    {
        try
        {
            File.WriteAllText(
                _statePath,
                JsonSerializer.Serialize(_blocked.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsComFailure(Exception ex) =>
        ex is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException;

    private string Explain(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
            return "Windows denied the firewall change. Run DoorNet as administrator.";

        if (ex is COMException { HResult: unchecked((int)0x80070005) })
            return "Windows denied the firewall change. Run DoorNet as administrator.";

        return ex.Message;
    }
}
