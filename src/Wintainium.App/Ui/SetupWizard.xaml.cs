using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Session;
using Wintainium.Core.Token;

namespace Wintainium.App.Ui;

/// <summary>The 5-step setup wizard: welcome, folders, GitHub token, scan, shortcuts (Show-SetupWizard).</summary>
public partial class SetupWizard : Window
{
    private readonly AppSession session;
    private readonly IShell shell;
    private readonly Func<Window, IReadOnlyList<string>, int> scan;
    private readonly Action<bool, bool> makeShortcuts;
    private readonly UIElement[] pages;
    private int page = 1;
    private int scanned;

    /// <param name="scan">Opens the scan window over the wizard with extra folders; returns how many apps were added.</param>
    /// <param name="makeShortcuts">Creates the Desktop and/or Start menu shortcut.</param>
    public SetupWizard(Window owner, AppSession session, IShell shell, Func<Window, IReadOnlyList<string>, int> scan, Action<bool, bool> makeShortcuts)
    {
        ArgumentNullException.ThrowIfNull(owner);
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        this.session = session;
        this.shell = shell;
        this.scan = scan;
        this.makeShortcuts = makeShortcuts;
        pages = [P1, P2, P3, P4, P5];
        InstallBox.Text = session.InstallRoot;
        DownloadBox.Text = session.DownloadDir;
        session.Token.ReadSaved(NullLog.Instance);
        TokenResult.Text = SetupSteps.TokenStatus(session.Token.HasSavedToken, session.Token.Expires);
        TokenResult.Foreground = Palette.Muted;
        SetPage(1);
    }

    /// <summary>Shows the wizard; closing or cancelling still counts as seen (N-08).</summary>
    public static void Run(Window owner, AppSession session, IShell shell, Func<Window, IReadOnlyList<string>, int> scan, Action<bool, bool> makeShortcuts)
    {
        new SetupWizard(owner, session, shell, scan, makeShortcuts).ShowDialog();
        SetupSteps.MarkSeen(session);
    }

    private void SetPage(int n)
    {
        page = n;
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = i + 1 == n ? Visibility.Visible : Visibility.Collapsed;
        }

        StepLabel.Text = SetupSteps.StepLabel(n);
        WBack.Visibility = n > 1 ? Visibility.Visible : Visibility.Hidden;
        WNext.Content = n == SetupSteps.Pages ? "Finish" : "Next";
    }

    private void WBack_Click(object sender, RoutedEventArgs e)
    {
        if (page > 1)
        {
            SetPage(page - 1);
        }
    }

    private void WNext_Click(object sender, RoutedEventArgs e)
    {
        if (page < SetupSteps.Pages)
        {
            SetPage(page + 1);
            return;
        }

        SetupSteps.Finish(session, InstallBox.Text, DownloadBox.Text);
        makeShortcuts(SCDesktop.IsChecked == true, SCStart.IsChecked == true);
        Close();
    }

    private void WCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BrowseInstall_Click(object sender, RoutedEventArgs e) => PickFolder(InstallBox);

    private void BrowseDownload_Click(object sender, RoutedEventArgs e) => PickFolder(DownloadBox);

    private void PickFolder(TextBox box)
    {
        var d = new OpenFolderDialog();
        string current = box.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current))
        {
            d.InitialDirectory = current;
        }

        if (d.ShowDialog(this) == true)
        {
            box.Text = d.FolderName;
        }
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => WebPage.Open(GitHubTokenCheck.TokenPageUrl, shell, session.Log);

    private async void CheckToken_Click(object sender, RoutedEventArgs e)
    {
        TokenResult.Text = "Checking with GitHub...";
        TokenResult.Foreground = Palette.Muted;
        CheckToken.IsEnabled = false;
        try
        {
            string plain = TokenBox.Password;
            (string text, bool saved) = await Task.Run(() => SetupSteps.CheckAndSaveTokenAsync(session.Http, session.Token, plain));
            TokenResult.Text = text;
            TokenResult.Foreground = saved ? Palette.Green : Palette.Red;
            if (saved)
            {
                TokenBox.Password = "";
            }
        }
#pragma warning disable CA1031 // saving can fail (disk, DPAPI): say so in the wizard
        catch (Exception ex)
#pragma warning restore CA1031
        {
            TokenResult.Text = $"{ex.Message} Nothing was saved.";
            TokenResult.Foreground = Palette.Red;
        }
        finally
        {
            CheckToken.IsEnabled = true;
        }
    }

    private void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        // the scan should look in the folder picked on step 2 as well
        string inst = InstallBox.Text.Trim();
        scanned += scan(this, inst.Length > 0 ? [inst] : []);
        ScanResultText.Text = SetupSteps.ScanResult(scanned);
    }
}
