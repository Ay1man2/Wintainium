using System.Globalization;

namespace Wintainium.Core.Session;

/// <summary>How a typed answer to a numbered choice is read, as v0.3.0 read it.</summary>
public static class ChoiceAnswer
{
    /// <summary>
    /// A number from 1 to <paramref name="count"/> picks that item. When the prompt says
    /// "Enter = 1", any other answer except x picks the first item (Update-App); otherwise it
    /// cancels (Select-Asset). Closing the dialog (null) always cancels.
    /// </summary>
    public static int? Parse(string? answer, int count, string prompt)
    {
        if (answer is null)
        {
            return null;
        }

        string a = answer.Trim();
        if (int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= count)
        {
            return n - 1;
        }

        bool enterIsFirst = (prompt ?? "").Contains("Enter = 1", StringComparison.Ordinal);
        if (!enterIsFirst || a.StartsWith('x') || a.StartsWith('X'))
        {
            return null;
        }

        return count > 0 ? 0 : null;
    }
}
