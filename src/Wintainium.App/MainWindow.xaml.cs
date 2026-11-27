using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Wintainium.App.Ui;
using Wintainium.Core;
using Wintainium.Core.Data;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Session;
using Wintainium.Core.Status;
using Wintainium.Core.Token;

namespace Wintainium.App;

/// <summary>The main window: toolbar, app grid, log pane and status bar (Show-Gui of v0.3.0).</summary>
public partial class MainWindow : Window
{
    private readonly string dataFolder;
    private readonly bool setupFirst;
    private readonly List<string> scanExtra = [];
    private readonly WindowLog log;
    private readonly WindowPrompts prompts;
    private readonly UiShell shell;
    private readonly DefenderThreatInfo threats = new();
    private Button[] buttons = [];
    private AppSession? session;
    private bool busy;
    private bool closeConfirmed;

    /// <param name="setupFirst">--setup: the wizard opens first even when setup was done.</param>
    public MainWindow(string dataFolder, bool setupFirst = false)
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} {AppInfo.Version}";
        this.dataFolder = dataFolder;
        this.setupFirst = setupFirst;
        log = new WindowLog(Dispatcher, LogScroll, StatusText);
        Log.ItemsSource = log.Lines;
        prompts = new WindowPrompts(this, log);
        shell = new UiShell(new WindowsShell(), Dispatcher);
        buttons = [BtnUpdate, BtnAll, BtnNotes, BtnLaunch, BtnPage, BtnFolder, BtnAdd, BtnRefresh, BtnForce, BtnSetup, BtnScan];
        PreviewKeyDown += Window_PreviewKeyDown;
        ContentRendered += Window_ContentRendered;
        Closing += Window_Closing;
        Closed += (_, _) => session?.Dispose();
    }

    /// <summary>The log pane, for errors nothing else caught (App.DispatcherUnhandledException).</summary>
    internal ILog WindowLog => log;

    /// <summary>Closing mid-update would cut a download or unpack short: ask first.</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy && !closeConfirmed)
        {
            closeConfirmed = prompts.Confirm($"Wintainium is still working ({StatusText.Text}). Closing now stops it part-way. Close anyway?");
            e.Cancel = !closeConfirmed;
        }
    }

    /// <summary>
    /// Runs a quick row action (launch, open a folder or page, gold). Windows can refuse (a UAC
    /// "No", a missing program): that lands in the log instead of closing the app. Ignored while busy.
    /// </summary>
    private void Quick(Action<AppRow> action)
    {
        if (busy)
        {
            return;
        }

        AppRow? r = RequireSelected();
        if (r is null)
        {
            return;
        }

        try
        {
            action(r);
        }
#pragma warning disable CA1031 // every error ends up in the log, as in v0.3.0
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write($"Error: {ex.Message}", LogColor.Red);
        }
    }

    private AppRow? Selected => Grid.SelectedItem as AppRow;

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        try
        {
            session = AppSession.Open(dataFolder, log, SecretProtector.ForCurrentUser(), self: new StatusEngine.SelfInfo(AppContext.BaseDirectory, AppInfo.Version));
        }
        catch (Exception ex) when (ex is AppListLoadException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        // M-13: the setup wizard comes first when setup was never done (or with --setup)
        if (setupFirst || !session.Settings.SetupDone)
        {
            ShowSetup();
        }

        await RunBusyAsync("Checking...", () => RefreshGridAsync(force: false));
    }

    /// <summary>Invoke-Busy: one action at a time, buttons off meanwhile, errors to the log.</summary>
    private async Task RunBusyAsync(string label, Func<Task> action)
    {
        if (busy || session is null)
        {
            return;
        }

        SetBusy(true, label);
        try
        {
            await action();
        }
#pragma warning disable CA1031 // every error ends up in the log, as in v0.3.0
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write($"Error: {ex.Message}", LogColor.Red);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private void SetBusy(bool on, string label)
    {
        busy = on;
        foreach (Button b in buttons)
        {
            b.IsEnabled = !on;
        }

        // the row menu, double-click, F5 and Delete are ignored too while busy
        RowMenu.IsEnabled = !on;

        log.SetStatus(on ? label : "Ready");
    }

    /// <summary>Update-Grid: looks everything up again and keeps the selected app selected.</summary>
    private async Task RefreshGridAsync(bool force)
    {
        AppSession s = session!;
        string? keep = Selected?.App;
        IReadOnlyList<AppRow> rows = await Task.Run(() => s.RefreshAsync(force, new DirectProgress(log.Progress)));
        Grid.ItemsSource = rows;
        ICollectionView view = CollectionViewSource.GetDefaultView(Grid.ItemsSource);
        view.Filter = o => o is AppRow r && RowViews.IsVisible(r, Search.Text, OnlyUpdates.IsChecked == true);
        if (keep is not null)
        {
            Grid.SelectedItem = rows.FirstOrDefault(r => r.App == keep);
        }

        (string text, StatusSummary.Tone tone) = StatusSummary.For(rows, DateTime.Now);
        Summary.Text = text;
        Summary.Foreground = tone switch
        {
            StatusSummary.Tone.Good => Palette.Green,
            StatusSummary.Tone.Updates => Palette.Amber,
            _ => Palette.Red,
        };
        s.WriteLookupNotes();
    }

    private void RefreshView()
    {
        if (Grid.ItemsSource is not null)
        {
            CollectionViewSource.GetDefaultView(Grid.ItemsSource).Refresh();
        }
    }

    private AppRow? RequireSelected() => RowActions.Require(Selected, log);

    private async Task UpdateSelectedAsync()
    {
        if (busy)
        {
            return;
        }

        AppRow? r = RequireSelected();
        if (r is null || session is null)
        {
            return;
        }

        await RunBusyAsync($"Updating {r.App}...", async () =>
        {
            if (RowActions.ConfirmUpdate(r, prompts))
            {
                Updater updater = session.Updater(prompts, shell, threats);
                await Task.Run(() => updater.UpdateAppAsync(r));
                await RefreshGridAsync(force: false);
            }
        });
    }

    private async Task RemoveSelectedAsync()
    {
        if (busy)
        {
            return;
        }

        AppRow? r = RequireSelected();
        if (r is null || session is null)
        {
            return;
        }

        await RunBusyAsync("Removing...", async () =>
        {
            if (RowActions.Remove(r, session, prompts))
            {
                await RefreshGridAsync(force: false);
            }
        });
    }

    private void ShowNotes() => Quick(r => new NotesWindow(this, RowViews.Notes(r), shell, log).ShowDialog());

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e) => await UpdateSelectedAsync();

    private async void BtnAll_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync("Updating all outdated apps...", async () =>
        {
            AppSession s = session!;
            Updater updater = s.Updater(prompts, shell, threats);
            IReadOnlyList<AppRow> rows = s.Rows;
            await Task.Run(() => updater.UpdateAllAsync(rows));
            await RefreshGridAsync(force: false);
        });

    private void BtnNotes_Click(object sender, RoutedEventArgs e) => ShowNotes();

    private void BtnLaunch_Click(object sender, RoutedEventArgs e) => Quick(r => RowActions.Launch(r, shell, log));

    private void BtnPage_Click(object sender, RoutedEventArgs e) => Quick(r => RowActions.OpenPage(r, shell, log));

    private void BtnFolder_Click(object sender, RoutedEventArgs e) => Quick(r => RowActions.OpenFolder(r, shell, log));

    private async void BtnAdd_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync("Adding an app...", async () =>
        {
            AddApp add = session!.AddApp(prompts, shell, threats);
            await Task.Run(() => add.RunAsync());
            await RefreshGridAsync(force: false);
        });

    // M-14: a refresh after Setup; after Scan only when something was added
    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session is null)
        {
            return;
        }

        if (ScanWindow.Run(this, session, scanExtra) > 0)
        {
            await RunBusyAsync("Checking...", () => RefreshGridAsync(force: false));
        }
    }

    private async void BtnSetup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session is null)
        {
            return;
        }

        ShowSetup();
        await RunBusyAsync("Checking...", () => RefreshGridAsync(force: false));
    }

    private void ShowSetup() => SetupWizard.Run(
        this,
        session!,
        shell,
        (owner, extra) =>
        {
            foreach (string f in extra.Where(f => !scanExtra.Contains(f)))
            {
                scanExtra.Add(f);
            }

            return ScanWindow.Run(owner, session!, scanExtra);
        },
        (desktop, start) => Shortcuts.Create(desktop, start, log));

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync("Checking...", () => RefreshGridAsync(force: false));

    private async void BtnForce_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync("Re-checking every release source...", () => RefreshGridAsync(force: true));

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(Search.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshView();
    }

    private async void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // a double-click on a column header or the scroll bar is not an update
        if (Selected is not null && RowUnder(e.OriginalSource) is not null)
        {
            await UpdateSelectedAsync();
        }
    }

    // K-04: right-click selects the row under the mouse, then the menu opens
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e.OriginalSource) is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }

    private static DataGridRow? RowUnder(object source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not DataGridRow)
        {
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }

        return d as DataGridRow;
    }

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        AppRow? r = RequireSelected();
        Visibility show = r is not null && r.Self ? Visibility.Visible : Visibility.Collapsed;
        GoldSep.Visibility = show;
        GoldItem.Visibility = show;
        GoldItem.IsChecked = session?.Settings.GoldSelf ?? true;
    }

    private void MenuPage_Click(object sender, RoutedEventArgs e) => Quick(r => RowActions.OpenPage(r, shell, log, quietWhenMissing: true));

    private void MenuFolder_Click(object sender, RoutedEventArgs e) => Quick(r => RowActions.OpenFolder(r, shell, log, quietWhenMissing: true));

    private async void MenuRemove_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync();

    private void GoldItem_Click(object sender, RoutedEventArgs e) => Quick(_ =>
    {
        if (session is not null)
        {
            RowActions.SetGold(session, GoldItem.IsChecked);
            RefreshView();
        }
    });

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RunBusyAsync("Checking...", () => RefreshGridAsync(force: false));
        }
        else if (e.Key == Key.Delete && Grid.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            await RemoveSelectedAsync();
        }
    }
}
