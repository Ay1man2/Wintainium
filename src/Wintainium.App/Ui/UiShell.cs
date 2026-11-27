using System.Windows.Threading;
using Wintainium.Core.Install;

namespace Wintainium.App.Ui;

/// <summary>Hands links, programs and folders to Windows from the window's thread, wherever the call came from.</summary>
public sealed class UiShell : IShell
{
    private readonly IShell inner;
    private readonly Dispatcher dispatcher;

    public UiShell(IShell inner, Dispatcher dispatcher)
    {
        this.inner = inner;
        this.dispatcher = dispatcher;
    }

    public void OpenUrl(Uri link) => OnUi(() => inner.OpenUrl(link));

    public void Start(string file, string workingDirectory) => OnUi(() => inner.Start(file, workingDirectory));

    public void ShowInExplorer(string file) => OnUi(() => inner.ShowInExplorer(file));

    public void OpenFolder(string folder) => OnUi(() => inner.OpenFolder(folder));

    public bool IsRunning(string processName) => inner.IsRunning(processName);

    private void OnUi(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}

/// <summary>Reports progress straight away, on the caller's thread (the log marshals it).</summary>
public sealed class DirectProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
