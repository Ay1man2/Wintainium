using System.Windows;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Session;

namespace Wintainium.App.Ui;

/// <summary>What's new: the release notes of one app (Show-Notes).</summary>
public partial class NotesWindow : Window
{
    private readonly NotesView view;
    private readonly IShell shell;
    private readonly ILog log;

    public NotesWindow(Window owner, NotesView view, IShell shell, ILog log)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(view);
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        this.view = view;
        this.shell = shell;
        this.log = log;
        NTitle.Text = view.Title;
        NSub.Text = view.Subtitle;
        NBody.Text = view.Body;
    }

    private void NOpen_Click(object sender, RoutedEventArgs e)
    {
        if (view.Page.Length > 0)
        {
            WebPage.Open(view.Page, shell, log);
        }
    }

    private void NClose_Click(object sender, RoutedEventArgs e) => Close();
}
