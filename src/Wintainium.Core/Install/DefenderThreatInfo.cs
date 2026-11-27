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

        using Process p = Process.Start(psi) ?? throw new UnauthorizedAccessException();
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(20000))
        {
            p.Kill();
            return null;
        }

        if (p.ExitCode != 0 || err.Result.Length > 0)
        {
            // most often "access denied" without admin rights; the caller says to run as admin
            throw new UnauthorizedAccessException(err.Result);
        }

        if (output.Trim().Length == 0 || JsonNode.Parse(output) is not JsonObject o)
        {
            return null;
        }

        return new ThreatDetection(o.GetString("name"), long.TryParse(o.GetString("id"), out long id) ? id : 0);
    }
}
