using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace DoorNet.App.Platform;

/// <summary>Tabs found in one browser process, plus the address of the active tab.</summary>
public sealed record BrowserTabInfo(int Count, IReadOnlyList<string> Titles, string? Address);

/// <summary>
/// Reads browser tabs through UI Automation: every top-level browser window exposes its
/// tab strip as TabItem elements whose name is the page title, and its address bar as an
/// Edit element. No history files and no network traffic: everything stays on the machine.
/// Runs on a background thread on a slow cadence because UIA walks are comparatively expensive.
/// </summary>
public static class BrowserTabsProbe
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "brave", "firefox", "opera", "vivaldi", "iexplore", "arc", "zen"
    };

    private static readonly Condition WindowCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);

    private static readonly Condition TabCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

    private static readonly Condition EditCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    /// <summary>Keys are executable names without extension (msedge, chrome, …).</summary>
    public static IReadOnlyDictionary<string, BrowserTabInfo> Probe()
    {
        var collected = new Dictionary<string, (List<string> Titles, int Count, string? Address)>(StringComparer.OrdinalIgnoreCase);

        AutomationElementCollection windows;
        try
        {
            windows = AutomationElement.RootElement.FindAll(TreeScope.Children, WindowCondition);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return new Dictionary<string, BrowserTabInfo>();
        }

        foreach (AutomationElement window in windows)
        {
            try
            {
                var exe = ProcessName(window.Current.ProcessId);
                if (exe is null || !Browsers.Contains(exe))
                    continue;

                var tabs = window.FindAll(TreeScope.Descendants, TabCondition);
                if (tabs.Count == 0)
                    continue;

                if (!collected.TryGetValue(exe, out var entry))
                    entry = (new List<string>(), 0, null);

                foreach (AutomationElement tab in tabs)
                {
                    entry.Count++;

                    var title = tab.Current.Name;
                    if (!string.IsNullOrWhiteSpace(title) && entry.Titles.Count < 30)
                        entry.Titles.Add(Trim(title));
                }

                // Only the active tab's address is exposed; the rest stay invisible.
                entry.Address ??= ReadAddressBar(window);

                collected[exe] = entry;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                           or COMException or ArgumentException)
            {
                // Window vanished mid-walk; skip it.
            }
        }

        return collected.ToDictionary(
            pair => pair.Key,
            pair => new BrowserTabInfo(pair.Value.Count, pair.Value.Titles, pair.Value.Address),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads the first edit control that holds a web address (the address bar).</summary>
    private static string? ReadAddressBar(AutomationElement window)
    {
        try
        {
            var edits = window.FindAll(TreeScope.Descendants, EditCondition);
            foreach (AutomationElement edit in edits)
            {
                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                    continue;

                if (pattern is ValuePattern value && TryUrl(value.Current.Value, out var url))
                    return url;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                       or COMException or ArgumentException or UnauthorizedAccessException)
        {
            // Page fields can vanish mid-walk; the next probe will try again.
        }

        return null;
    }

    private static bool TryUrl(string? value, out string url)
    {
        url = string.Empty;

        var text = (value ?? string.Empty).Trim();
        if (text.Length is 0 or > 2048 || text.Contains(' '))
            return false;

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = text;
            return true;
        }

        // Address bars hide the scheme for plain sites: "example.com/path" is a real URL,
        // while "localhost:5080" or an intranet name is not worth scoring.
        var host = text.Split('/', '?', '#')[0];
        if (!host.Contains('.') || host.StartsWith('.') || host.EndsWith('.'))
            return false;

        if (!host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']'))
            return false;

        url = "https://" + text;
        return true;
    }

    private static string? ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Trim(string title)
    {
        var clean = title.Trim();
        return clean.Length <= 80 ? clean : clean[..77] + "...";
    }
}
