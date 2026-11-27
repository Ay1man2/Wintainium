using System.Windows;
using Wintainium.Core.Install;

namespace Wintainium.App.Ui;

/// <summary>
/// The question box (Show-Prompt): recent log lines for context, the question, and Yes / No
/// (/ Stop) buttons or a text box with OK / Cancel.
/// </summary>
public partial class PromptDialog : Window
{
    private readonly bool textMode;
    private string? text;
    private YesNoStop answer = YesNoStop.No; // closing the box with X or Alt+F4 answers No, never Yes

    private PromptDialog(Window? owner, string context, string question, bool textMode, bool offerStop)
    {
        InitializeComponent();
        if (owner is not null && owner.IsVisible)
        {
            Owner = owner;
            Icon = owner.Icon;
        }

        this.textMode = textMode;
        Ctx.Text = context;
        if (context.Length == 0)
        {
            Ctx.Visibility = Visibility.Collapsed;
        }

        PromptText.Text = question.Trim();
        if (textMode)
        {
            B1.Content = "OK";
            B1.IsDefault = true;
            B2.Content = "Cancel";
            B2.IsCancel = true;
            B3.Visibility = Visibility.Collapsed;
            ContentRendered += (_, _) => Input.Focus();
        }
        else
        {
            Input.Visibility = Visibility.Collapsed;
            B1.Content = "Yes";
            B2.Content = "No";
            B2.IsCancel = true;
            if (offerStop)
            {
                B3.Content = "Stop";
            }
            else
            {
                B3.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Yes / No (/ Stop). Closing the box is No.</summary>
    public static YesNoStop Ask(Window? owner, string context, string question, bool offerStop)
    {
        var d = new PromptDialog(owner, context, question, textMode: false, offerStop);
        d.ShowDialog();
        return d.answer;
    }

    /// <summary>A typed answer, or null when cancelled or closed.</summary>
    public static string? AskText(Window? owner, string context, string question)
    {
        var d = new PromptDialog(owner, context, question, textMode: true, offerStop: false);
        d.ShowDialog();
        return d.text;
    }

    private void B1_Click(object sender, RoutedEventArgs e)
    {
        if (textMode)
        {
            text = Input.Text;
        }
        else
        {
            answer = YesNoStop.Yes;
        }

        Close();
    }

    private void B2_Click(object sender, RoutedEventArgs e)
    {
        answer = YesNoStop.No;
        text = null;
        Close();
    }

    private void B3_Click(object sender, RoutedEventArgs e)
    {
        answer = YesNoStop.Stop;
        Close();
    }
}
