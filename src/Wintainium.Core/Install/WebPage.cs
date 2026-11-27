using Wintainium.Core.Logging;
using Wintainium.Core.Releases;

namespace Wintainium.Core.Install;

/// <summary>Opens release pages and help links (Open-WebPage): absolute https links only.</summary>
public static class WebPage
{
    public static bool Open(string? url, IShell shell, ILog log)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(log);
        Uri? u = Links.ParseHttps(url);
        if (u is not null)
        {
            // Open the parsed form, so Windows gets exactly the https link that was checked.
            shell.OpenUrl(u);
            return true;
        }

        log.Write($"Not opening '{Links.StripUserInfo(url ?? "")}': it isn't an https link.", LogColor.Yellow);
        return false;
    }
}
