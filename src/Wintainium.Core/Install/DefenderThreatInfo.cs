using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Wintainium.Core.Json;

namespace Wintainium.Core.Install;

/// <summary>
/// Windows Security's latest detection for a file name, read with the Defender cmdlets in
/// Windows PowerShell (Get-MpThreatDetection, Get-MpThreat), as v0.3.0 did. Only reads.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefenderThreatInfo : IThreatInfo
{
    private const string Query =
        "$ErrorActionPreference = 'Stop'; $n = $env:WINTAINIUM_THREAT_FILE; " +
        "$d = Get-MpThreatDetection | Where-Object { \"$($_.Resources)\" -match [regex]::Escape($n) } | Sort-Object InitialDetectionTime -Descending | Select-Object -First 1; " +
        "if ($d) { $t = Get-MpThreat -ThreatID $d.ThreatID | Select-Object -First 1; [pscustomobject]@{ name = [string]$t.ThreatName; id = [long]$d.ThreatID } | ConvertTo-Json -Compress }";

    /// <summary>How long the query may take before it is stopped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public ThreatDetection? FindRecent(string fileName)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment.Remove("PSModulePath");
        psi.Environment["WINTAINIUM_THREAT_FILE"] = fileName;
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-Command", Query })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        Task<string> err = p.StandardError.ReadToEndAsync();
        Task<string> outText = p.StandardOutput.ReadToEndAsync();
        Task exited = p.WaitForExitAsync();

        // both streams are read while waiting, so a full pipe can't hold the query up past the timeout
        if (!Task.WaitAll([err, outText, exited], Timeout))
        {
            ProcessRunner.KillTree(p);
            return null;
        }

        if (p.ExitCode != 0 || PowerShellStderr.IsError(err.Result))
        {
            // most often "access denied" without admin rights; the caller says to run as admin
            throw new UnauthorizedAccessException(err.Result);
        }

        string output = outText.Result;
        if (output.Trim().Length == 0 || JsonNode.Parse(output) is not JsonObject o)
        {
            return null;
        }

        return new ThreatDetection(o.GetString("name"), long.TryParse(o.GetString("id"), out long id) ? id : 0);
    }
}

/// <summary>Reading what Windows PowerShell wrote to stderr.</summary>
internal static class PowerShellStderr
{
    /// <summary>
    /// True when stderr holds an error. Windows PowerShell also writes progress records there as
    /// "#&lt; CLIXML" text, which is not an error unless it holds an Error record.
    /// </summary>
    internal static bool IsError(string stderr)
    {
        string s = (stderr ?? "").Trim();
        if (s.StartsWith("#< CLIXML", StringComparison.Ordinal))
        {
            return s.Contains("S=\"Error\"", StringComparison.Ordinal);
        }

        return s.Length > 0;
    }
}
