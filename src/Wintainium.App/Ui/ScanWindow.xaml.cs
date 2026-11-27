using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wintainium.Core.Scan;
using Wintainium.Core.Session;

namespace Wintainium.App.Ui;

/// <summary>Find apps you already have (Show-ScanWindow): runs the PC scan and adds what the user ticks.</summary>
public partial class ScanWindow : Window
{
    private static readonly Brush Amber = (Brush)new BrushConverter().ConvertFromString("#F9E2AF")!;
    private static readonly Brush Card = (Brush)new BrushConverter().ConvertFromString("#1E1E2E")!;
    private static readonly Brush Field = (Brush)new BrushConverter().ConvertFromString("#313244")!;
    private static readonly Brush FieldEdge = (Brush)new BrushConverter().ConvertFromString("#45475A")!;

    private readonly AppSession session;
    private readonly List<string> extraRoots;
    private readonly List<(ScanPick Pick, CheckBox Check, TextBox Name, TextBlock Path, Border Border)> rows = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? scanCancel;
    private Stopwatch scanClock = new();
    private int hidden;
    private bool settingCheck;

    private ScanWindow(Window owner, AppSession session, List<string> extraRoots)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        this.session = session;
        this.extraRoots = extraRoots;
        timer.Tick += (_, _) => ShowScanning();
        ContentRendered += async (_, _) => await StartScanAsync();
        Closing += (_, _) => scanCancel?.Cancel(); // closing stops the scan (C-01)
    }

    public int Added { get; private set; }

    private void ShowScanning()
    {
        SStatus.Text = $"Scanning your PC... {(int)scanClock.Elapsed.TotalSeconds} s (usually under a minute)";
        SStatus.Foreground = Amber;
    }

    /// <summary>Opens the scan window and returns how many apps were added.</summary>
    /// <param name="extraRoots">Folders added with Scan another folder...; kept for the next scan.</param>
    public static int Run(Window owner, AppSession session, List<string> extraRoots)
    {
        var w = new ScanWindow(owner, session, extraRoots);
        w.ShowDialog();
        return w.Added;
    }

    private async Task StartScanAsync()
    {
        if (scanCancel is not null)
        {
            return;
        }

        SList.Children.Clear();
        rows.Clear();
        SFolder.IsEnabled = false;
        SHidden.IsEnabled = false;
        SAdd.IsEnabled = false;
        SExtra.Text = "";
        hidden = 0;
        SHidden.Visibility = Visibility.Collapsed;
        using var cancel = new CancellationTokenSource();
        scanCancel = cancel;
        scanClock = Stopwatch.StartNew();
        timer.Start();
        ShowScanning();
        ScanResult? res = null;
        string? why = null;
        try
        {
            res = await session.ScanAsync(extraRoots, AppContext.BaseDirectory, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // C-08: any failure is shown in the window
        catch (Exception ex)
#pragma warning restore CA1031
        {
            why = ex.Message;
        }
        finally
        {
            timer.Stop();
            scanCancel = null;
        }

        if (!IsLoaded)
        {
            return;
        }

        SFolder.IsEnabled = true;
        SHidden.IsEnabled = true;
        if (res is null)
        {
            SStatus.Text = $"The scan didn't finish: {why ?? "no results came back"}";
            SStatus.Foreground = Palette.Red;
            return;
        }

        foreach (ScanCandidate c in res.Candidates)
        {
            AddRow(new ScanPick(c));
        }

        hidden = res.Hidden;
        SExtra.Text = ScanPicker.Footer(res.ModCount, extraRoots);
        UpdateSummary();
    }

    private void AddRow(ScanPick pick)
    {
        var border = new Border { Background = Card, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 12, 0), IsChecked = pick.Checked };
        Grid.SetColumn(check, 0);
        var stack = new StackPanel();
        Grid.SetColumn(stack, 1);
        var nameBox = new TextBox
        {
            Text = pick.Name,
            Background = Field,
            Foreground = Palette.Default,
            BorderBrush = FieldEdge,
            CaretBrush = Palette.Default,
            Padding = new Thickness(5, 3, 5, 3),
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 360,
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Name shown in your list",
        };
        nameBox.TextChanged += (_, _) => pick.Name = nameBox.Text;
        stack.Children.Add(nameBox);
        TextBlock Line(string text, Brush color, double size)
        {
            var t = new TextBlock { Text = text, Foreground = color, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            stack.Children.Add(t);
            return t;
        }

        Line(pick.Candidate.From, Palette.Muted, 12);
        TextBlock path;
        if (!pick.NeedsLocate)
        {
            path = Line(pick.Exe, Palette.Dim, 11);
            if (pick.Candidate.Note.Length > 0)
            {
                Line(pick.Candidate.Note, Palette.Dim, 11);
            }
        }
        else
        {
            // needs one more step: an amber edge and an amber line that says what to do
            border.BorderBrush = Amber;
            border.BorderThickness = new Thickness(3, 0, 0, 0);
            path = Line(ScanPicker.NotFoundYet, Amber, 11);
            if (pick.Candidate.Note.Length > 0)
            {
                Line(ScanPicker.LocateNote(pick.Candidate.Note), Palette.Dim, 11);
            }

            check.ToolTip = "Pick the installed program first (this opens Locate .exe...)";
        }

        var row = (pick, check, nameBox, path, border);
        check.Checked += (_, _) =>
        {
            if (settingCheck)
            {
                return;
            }

            // ticking an entry whose program wasn't found opens the Locate picker
            if (pick.NeedsLocate && !Locate(row))
            {
                settingCheck = true;
                check.IsChecked = false;
                settingCheck = false;
            }

            pick.Checked = check.IsChecked == true;
            UpdateCount();
        };
        check.Unchecked += (_, _) =>
        {
            pick.Checked = false;
            UpdateCount();
        };

        if (pick.Candidate.Kind == CandidateKind.Download)
        {
            var buttons = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            var locate = new Button { Style = (Style)FindResource("Primary"), Content = "Locate .exe...", ToolTip = "Pick the program you installed from this download" };
            locate.Click += (_, _) => Locate(row);
            buttons.Children.Add(locate);
            if (pick.Candidate.Key.Length > 0)
            {
                var hide = new Button
                {
                    Style = (Style)FindResource("Btn"),
                    Content = "Don't show again",
                    Margin = new Thickness(0, 6, 0, 0),
                    ToolTip = "Leave this out of future scans (Show hidden brings it back)",
                };
                hide.Click += (_, _) => Hide(row);
                buttons.Children.Add(hide);
            }

            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
        }

        grid.Children.Add(check);
        grid.Children.Add(stack);
        border.Child = grid;
        SList.Children.Add(border);
        rows.Add(row);
    }

    /// <summary>Asks where a downloaded app was installed; true once a program is picked (Invoke-ScanLocate).</summary>
    private bool Locate((ScanPick Pick, CheckBox Check, TextBox Name, TextBlock Path, Border Border) r)
    {
        var d = new OpenFileDialog
        {
            Title = $"Where is {r.Name.Text} installed? Pick its program (.exe)",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (d.ShowDialog(this) != true)
        {
            return false;
        }

        r.Pick.Locate(d.FileName);
        r.Path.Text = d.FileName;
        r.Path.Foreground = Palette.Dim;
        r.Border.BorderThickness = new Thickness(0);
        r.Check.ToolTip = null;
        settingCheck = true;
        r.Check.IsChecked = true;
        settingCheck = false;
        UpdateSummary();
        return true;
    }

    private void Hide((ScanPick Pick, CheckBox Check, TextBox Name, TextBlock Path, Border Border) r)
    {
        ScanPicker.Hide(r.Pick, session.Settings);
        SList.Children.Remove(r.Border);
        rows.Remove(r);
        hidden++;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        (string text, bool good) = ScanPicker.Summary([.. rows.Select(r => r.Pick)]);
        SStatus.Text = text;
        SStatus.Foreground = good ? Palette.Green : Amber;
        SHidden.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        SHidden.Content = $"Show hidden ({hidden})";
        UpdateCount();
    }

    private void UpdateCount()
    {
        int n = rows.Count(r => r.Pick.Checked);
        SAdd.Content = ScanPicker.AddLabel(n);
        SAdd.IsEnabled = n > 0 && scanCancel is null;
    }

    private void SAdd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Added += ScanPicker.AddSelected(rows.Select(r => r.Pick), session.List, session.Log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing was added; the window stays open so the user can try again
            SStatus.Text = $"Could not save the app list: {ex.Message}";
            SStatus.Foreground = Palette.Red;
            return;
        }

        Close();
    }

    private async void SHidden_Click(object sender, RoutedEventArgs e)
    {
        ScanPicker.ShowHidden(session.Settings);
        await StartScanAsync();
    }

    private async void SFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Pick a folder where you keep apps, emulators or games" };
        if (d.ShowDialog(this) == true && !extraRoots.Contains(d.FolderName))
        {
            extraRoots.Add(d.FolderName);
            await StartScanAsync();
        }
    }

    private void SClose_Click(object sender, RoutedEventArgs e) => Close();
}
