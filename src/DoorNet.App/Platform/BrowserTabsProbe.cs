using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace DoorNet.App.Platform;

/// <summary>Tabs found in one browser process.</summary>
public sealed record BrowserTabInfo(int Count, IReadOnlyList<string> Titles);

/// <summary>
/// Reads browser tabs through UI Automation: every top-level browser window exposes its
/// tab strip as TabItem elements whose name is the page title. No history files, no URLs,
/// nothing leaves the machine. Runs on a background thread on a slow cadence because UIA
/// walks are comparatively expensive.
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

    /// <summary>Keys are executable names without extension (msedge, chrome, …).</summary>
    public static IReadOnlyDictionary<string, BrowserTabInfo> Probe()
    {
        var collected = new Dictionary<string, (List<string> Titles, int Count)>(StringComparer.OrdinalIgnoreCase);

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
                    entry = (new List<string>(), 0);

                foreach (AutomationElement tab in tabs)
                {
                    entry.Count++;

                    var title = tab.Current.Name;
                    if (!string.IsNullOrWhiteSpace(title) && entry.Titles.Count < 30)
                        entry.Titles.Add(Trim(title));
                }

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
            pair => new BrowserTabInfo(pair.Value.Count, pair.Value.Titles),
            StringComparer.OrdinalIgnoreCase);
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
