using System.Globalization;
using System.Windows;
using Wintainium.Core.Install;
using Wintainium.Core.Logging;
using Wintainium.Core.Session;

namespace Wintainium.App.Ui;

/// <summary>Every question becomes a dialog over the main window (the Read-Host override of v0.3.0).</summary>
public sealed class WindowPrompts : IUserPrompts
{
    private readonly Window owner;
    private readonly WindowLog log;

    public WindowPrompts(Window owner, WindowLog log)
    {
        this.owner = owner;
        this.log = log;
    }

    public bool Confirm(string question) => OnUi(() => PromptDialog.Ask(owner, log.TakePromptContext(), question, offerStop: false)) == YesNoStop.Yes;

    public YesNoStop AskYesNoStop(string question) => OnUi(() => PromptDialog.Ask(owner, log.TakePromptContext(), question, offerStop: true));

    public string Ask(string question) => OnUi(() => PromptDialog.AskText(owner, log.TakePromptContext(), question)) ?? "";

    /// <summary>The title and numbered items go to the log (and so into the dialog's context), then the number is asked.</summary>
    public int? Choose(string title, IReadOnlyList<string> items, string prompt)
    {
        ArgumentNullException.ThrowIfNull(items);
        log.Write("  " + title);
        for (int i = 0; i < items.Count; i++)
        {
            log.Write(string.Format(CultureInfo.InvariantCulture, "   {0,2}  {1}", i + 1, items[i]));
        }

        string? answer = OnUi(() => PromptDialog.AskText(owner, log.TakePromptContext(items.Count + 1), prompt));
        return ChoiceAnswer.Parse(answer, items.Count, prompt);
    }

    private T OnUi<T>(Func<T> ask) => owner.Dispatcher.CheckAccess() ? ask() : owner.Dispatcher.Invoke(ask);
}
