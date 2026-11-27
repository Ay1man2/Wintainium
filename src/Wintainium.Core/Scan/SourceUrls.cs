using System.Text.RegularExpressions;

namespace Wintainium.Core.Scan;

/// <summary>Where something came from: a code-hosting site with owner/repo, or an itch.io game page.</summary>
public sealed record RepoInfo(string Site, string Repo, string Url = "");

/// <summary>Turns GitHub's numeric repository IDs (in release-asset links) back into owner/repo.</summary>
public interface IRepoIdResolver
{
    /// <summary>owner/repo, or "" when unknown.</summary>
    string Resolve(string id);
}

/// <summary>Recognizes code-hosting and itch.io links (Get-RepoFromUrl).</summary>
public static partial class SourceUrls
{
    /// <summary>owner/repo plus the site when <paramref name="url"/> points at a code host or an itch.io game; else null.</summary>
    public static RepoInfo? Parse(string? url, IRepoIdResolver? ids = null)
    {
        if (string.IsNullOrEmpty(url) || !HttpScheme().IsMatch(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? u))
        {
            return null;
        }

        string site = u.Host.ToLowerInvariant();
        string[] parts = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        Match itch = ItchHost().Match(site);
        if (itch.Success)
        {
            // a game page (dev.itch.io/game); itch.io's file servers carry no page, so they don't count
            if (parts.Length >= 1 && itch.Groups[1].Value != "www")
            {
                return new RepoInfo("itch.io", "", $"https://{site}/{parts[0]}");
            }

            return null;
        }

        if (site.EndsWith("githubusercontent.com", StringComparison.Ordinal))
        {
            // raw.githubusercontent.com/owner/repo/...; release-asset hosts carry no repo
            if (site.StartsWith("raw.", StringComparison.Ordinal) && parts.Length >= 2)
            {
                return new RepoInfo("github.com", $"{parts[0]}/{parts[1]}");
            }

            Match asset = ReleaseAssetPath().Match(u.AbsolutePath);
            if (asset.Success)
            {
                return new RepoInfo("github.com", ids?.Resolve(asset.Groups[1].Value) ?? "");
            }

            return new RepoInfo("github.com", "");
        }

        if (!ForgeHosts().IsMatch(site) && !ReleasePath().IsMatch(u.AbsolutePath))
        {
            return null;
        }

        if (parts.Length < 2)
        {
            return new RepoInfo(site, "");
        }

        if (site == "gitlab.com")
        {
            int cut = Array.IndexOf(parts, "-");
            string[] p = cut > 1 ? parts[..cut] : parts[..2];
            return new RepoInfo(site, string.Join('/', p));
        }

        return new RepoInfo(site, $"{parts[0]}/{parts[1]}");
    }

    /// <summary>A git remote as a web link: git@host:owner/repo.git -> https://host/owner/repo.</summary>
    public static string FromGitRemote(string remote) =>
        DotGit().Replace(SshRemote().Replace(remote ?? "", "https://$1/"), "");

    [GeneratedRegex("^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex HttpScheme();

    [GeneratedRegex(@"^([\w-]+)\.itch\.io$")]
    private static partial Regex ItchHost();

    [GeneratedRegex(@"/github-production-release-asset(?:-[0-9a-f]+)?/(\d+)/", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseAssetPath();

    [GeneratedRegex(@"(^|\.)(github\.com|githubusercontent\.com|gitlab\.com|codeberg\.org|bitbucket\.org|sourceforge\.net|gitea\.com)$|^git\.", RegexOptions.IgnoreCase)]
    private static partial Regex ForgeHosts();

    [GeneratedRegex("/releases/(download|tag)/|/-/releases", RegexOptions.IgnoreCase)]
    private static partial Regex ReleasePath();

    [GeneratedRegex("^git@([^:]+):")]
    private static partial Regex SshRemote();

    [GeneratedRegex(@"\.git$")]
    private static partial Regex DotGit();
}
