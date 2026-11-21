using System.IO;
using System.Runtime.InteropServices;
using DoorNet.Core.Firewall;

namespace DoorNet.Capture;

/// <summary>
/// Blocks an application's traffic with Windows Defender Firewall rules — one inbound and
/// one outbound per executable, controllable independently.
/// <para>
/// GlassWire enforces its firewall with its own WFP driver; DoorNet deliberately does not.
/// OS rules need no kernel component, survive reboots, are visible in Windows Firewall and
/// can be removed without reinstalling anything — at the cost of requiring elevation and
/// only taking effect while Windows Firewall is enforcing.
/// </para>
/// </summary>
public sealed class WindowsFirewallController : IFirewallController
{
    private const string PolicyProgId = "HNetCfg.FwPolicy2";
    private const string RuleProgId = "HNetCfg.FwRule";

    private const int RuleDirectionIn = 1;
    private const int RuleDirectionOut = 2;
    private const int RuleActionBlock = 0;
    private const int ProtocolAny = 256;
    private const int ProfileAll = int.MaxValue;

    private static readonly FirewallDirection[] BothDirections =
        [FirewallDirection.Out, FirewallDirection.In];

    private readonly object _sync = new();
    private readonly Dictionary<string, BlockFlags> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _statePath;

    private object? _policy;
    private object? _rules;
    private bool _comUnavailable;
    private bool _enabled = true;

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
                return _blocked.Keys.ToArray();
        }
    }

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
                return _enabled;
        }
    }

    public bool IsBlocked(string? executablePath)
    {
        var normalized = Normalise(executablePath);
        if (normalized is null)
            return false;

        lock (_sync)
            return _blocked.TryGetValue(normalized, out var flags) && flags.Any;
    }

    public bool IsBlocked(string? executablePath, FirewallDirection direction)
    {
        var normalized = Normalise(executablePath);
        if (normalized is null)
            return false;

        lock (_sync)
            return _blocked.TryGetValue(normalized, out var flags) && flags.Get(direction);
    }

    public FirewallChangeResult SetEnabled(bool enabled)
    {
        if (!IsAvailable)
            return FirewallChangeResult.Fail(FirewallChangeStatus.NotElevated, UnavailableReason!);

        lock (_sync)
        {
            try
            {
                _enabled = enabled;

                foreach (var (path, flags) in _blocked)
                {
                    foreach (var direction in BothDirections)
                    {
                        if (!flags.Get(direction))
                            continue;

                        var name = FirewallRuleNames.ForPath(path, direction);
                        if (!RuleExists(Rules, name))
                            continue;

                        dynamic rule = Rules.Item(name);
                        rule.Enabled = enabled;
                    }
                }

                SaveState();
                return FirewallChangeResult.Ok(enabled ? "Firewall rules enabled." : "Firewall rules suspended.");
            }
            catch (Exception ex)
            {
                _enabled = !enabled;
                return FirewallChangeResult.Fail(FirewallChangeStatus.Failed, Explain(ex));
            }
        }
    }

    public FirewallChangeResult SetBlocked(string? executablePath, FirewallDirection direction, bool blocked) =>
        Apply(
            executablePath,
            inbound: direction == FirewallDirection.In ? blocked : null,
            outbound: direction == FirewallDirection.Out ? blocked : null);

    public FirewallChangeResult Block(string? executablePath) =>
        Apply(executablePath, inbound: true, outbound: true);

    public FirewallChangeResult Allow(string? executablePath) =>
        Apply(executablePath, inbound: false, outbound: false);

    private FirewallChangeResult Apply(string? executablePath, bool? inbound, bool? outbound)
    {
        var path = Normalise(executablePath);
        if (path is null)
            return FirewallChangeResult.Fail(FirewallChangeStatus.InvalidPath, "The executable path is not available for this process.");

        if (!IsAvailable)
            return FirewallChangeResult.Fail(FirewallChangeStatus.NotElevated, UnavailableReason!);

        // Allowing an app that has been uninstalled must still remove its rules.
        var blocking = inbound == true || outbound == true;
        if (blocking && !File.Exists(path))
            return FirewallChangeResult.Fail(FirewallChangeStatus.InvalidPath, "The executable no longer exists on disk.");

        lock (_sync)
        {
            try
            {
                if (!_blocked.TryGetValue(path, out var flags))
                    flags = new BlockFlags();

                if (inbound.HasValue)
                    flags.In = inbound.Value;

                if (outbound.HasValue)
                    flags.Out = outbound.Value;

                foreach (var direction in BothDirections)
                {
                    var name = FirewallRuleNames.ForPath(path, direction);

                    if (flags.Get(direction))
                    {
                        EnsureRule(Rules, path, direction);

                        dynamic rule = Rules.Item(name);
                        if ((bool)rule.Enabled != _enabled)
                            rule.Enabled = _enabled;
                    }
                    else
                    {
                        RemoveRule(Rules, name);
                    }
                }

                if (flags.Any)
                    _blocked[path] = flags;
                else
                    _blocked.Remove(path);

                SaveState();
                return FirewallChangeResult.Ok($"Firewall rules updated for {Path.GetFileName(path)}.");
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

    private void EnsureRule(dynamic rules, string path, FirewallDirection direction)
    {
        var name = FirewallRuleNames.ForPath(path, direction);
        if (RuleExists(rules, name))
            return;

        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID(RuleProgId)!)!;
        rule.Name = name;
        rule.Description = "Blocked by DoorNet";
        rule.Grouping = FirewallRuleNames.Group;
        rule.Direction = direction == FirewallDirection.Out ? RuleDirectionOut : RuleDirectionIn;
        rule.Action = RuleActionBlock;
        rule.Enabled = _enabled;
        rule.InterfaceTypes = "All";
        rule.Profiles = ProfileAll;
        rule.Protocol = ProtocolAny;
        rule.ApplicationName = path;

        rules.Add(rule);
    }

    private static void RemoveRule(dynamic rules, string name)
    {
        if (RuleExists(rules, name))
            rules.Remove(name);
    }

    /// <summary>
    /// Reconciles stored state with the live rules: upgrades legacy rules, creates missing
    /// direction rules, removes rules for directions that are no longer blocked, and
    /// re-applies the master switch. Requires elevation, because reading rules needs admin.
    /// </summary>
    private void VerifyRulesIfPossible()
    {
        if (!IsAvailable)
            return;

        lock (_sync)
        {
            try
            {
                foreach (var (path, flags) in _blocked)
                {
                    // Rules created before per-direction control had no suffix.
                    RemoveRule(Rules, FirewallRuleNames.ForPath(path));

                    foreach (var direction in BothDirections)
                    {
                        if (!flags.Get(direction))
                        {
                            RemoveRule(Rules, FirewallRuleNames.ForPath(path, direction));
                            continue;
                        }

                        EnsureRule(Rules, path, direction);

                        dynamic rule = Rules.Item(FirewallRuleNames.ForPath(path, direction));
                        if ((bool)rule.Enabled != _enabled)
                            rule.Enabled = _enabled;
                    }
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

            var state = FirewallState.Parse(File.ReadAllText(_statePath));

            _enabled = state.Enabled;

            foreach (var entry in state.Blocked)
            {
                if (Normalise(entry.Path) is { } normalized)
                    _blocked[normalized] = new BlockFlags { In = entry.In, Out = entry.Out };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveState()
    {
        try
        {
            var state = new FirewallState
            {
                Enabled = _enabled,
                Blocked = _blocked
                    .Select(pair => new FirewallEntry(pair.Key, pair.Value.In, pair.Value.Out))
                    .ToArray()
            };

            File.WriteAllText(_statePath, state.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Explain(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
            return "Windows denied the firewall change. Run DoorNet as administrator.";

        if (ex is COMException { HResult: unchecked((int)0x80070005) })
            return "Windows denied the firewall change. Run DoorNet as administrator.";

        return ex.Message;
    }

    private sealed class BlockFlags
    {
        public bool In;
        public bool Out;

        public bool Any => In || Out;

        public bool Get(FirewallDirection direction) =>
            direction == FirewallDirection.In ? In : Out;
    }
}
