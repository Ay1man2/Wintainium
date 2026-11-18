// Wintainium.exe - a small launcher so people can double-click instead of running PowerShell.
// It starts Wintainium.ps1 (next to this exe) in Windows PowerShell with the window ("gui")
// and shows a message if it fails to start. All of Wintainium's logic stays in the .ps1,
// which anyone can read. Built by build\Build-Release.ps1 with the C# compiler that ships
// with Windows (.NET Framework 4), so no SDK is needed. Written for C# 5 on purpose.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("Wintainium")]
[assembly: AssemblyProduct("Wintainium")]
[assembly: AssemblyDescription("Obtainium-style app manager for Windows (launcher for Wintainium.ps1)")]
[assembly: AssemblyCopyright("GPL-3.0 - https://github.com/Ay1man2/Wintainium")]
[assembly: AssemblyVersion("0.0.0.0")]
[assembly: AssemblyFileVersion("0.0.0.0")]

static class Launcher
{
    static string Quote(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    [STAThread]
    static int Main(string[] args)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string script = Path.Combine(dir, "Wintainium.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show("Wintainium.ps1 was not found next to Wintainium.exe.\n\n" +
                            "Keep Wintainium.exe in the same folder as the other files from the zip.",
                            "Wintainium", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                 @"WindowsPowerShell\v1.0\powershell.exe");
        StringBuilder rest = new StringBuilder();
        if (args.Length == 0) rest.Append("gui");
        foreach (string a in args) { if (rest.Length > 0) rest.Append(' '); rest.Append(Quote(a)); }

        ProcessStartInfo psi = new ProcessStartInfo(ps,
            "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File " + Quote(script) + " " + rest);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = dir;
        psi.RedirectStandardError = true;

        StringBuilder err = new StringBuilder();
        try
        {
            Process p = Process.Start(psi);
            p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0 && err.Length > 0)
            {
                string text = err.ToString();
                if (text.Length > 1500) text = text.Substring(0, 1500) + "...";
                MessageBox.Show("Wintainium stopped with an error:\n\n" + text, "Wintainium",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start Windows PowerShell:\n\n" + ex.Message, "Wintainium",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
