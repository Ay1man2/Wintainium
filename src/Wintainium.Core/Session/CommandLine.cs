namespace Wintainium.Core.Session;

/// <summary>
/// The few command-line options v0.4.0 keeps (L-01 to L-05). No arguments opens the window.
/// </summary>
public sealed record CommandLine
{
    public const string Usage = "Usage: Wintainium.exe [--list] [--setup] [--data <folder>] [--uninstall] [--install-only]";

    /// <summary>Print the table to the console and exit (L-02).</summary>
    public bool List { get; init; }

    /// <summary>Open the window with the setup wizard first (L-03).</summary>
    public bool Setup { get; init; }

    /// <summary>Folder with the data files, instead of the app's own folder (L-05).</summary>
    public string? Data { get; init; }

    public bool Uninstall { get; init; }

    public bool InstallOnly { get; init; }

    /// <summary>Set when the arguments can't be used; the text says why.</summary>
    public string? Error { get; init; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var c = new CommandLine();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string name = a;
            string? value = null;
            int eq = a.IndexOf('=', StringComparison.Ordinal);
            if (a.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                name = a[..eq];
                value = a[(eq + 1)..];
            }

            string flag = name.ToLowerInvariant();
            if (value is not null && flag is "--list" or "--setup" or "--uninstall" or "--install-only")
            {
                return new CommandLine { Error = $"{name} doesn't take a value." };
            }

            switch (flag)
            {
                case "--list":
                    c = c with { List = true };
                    break;
                case "--setup":
                    c = c with { Setup = true };
                    break;
                case "--uninstall":
                    c = c with { Uninstall = true };
                    break;
                case "--install-only":
                    c = c with { InstallOnly = true };
                    break;
                case "--data":
                    if (value is null)
                    {
                        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            return new CommandLine { Error = "--data needs a folder." };
                        }

                        value = args[++i];
                    }

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return new CommandLine { Error = "--data needs a folder." };
                    }

                    c = c with { Data = value.Trim().Trim('"') };
                    break;
                default:
                    return new CommandLine { Error = $"Unknown option: {a}" };
            }
        }

        if (c.InstallOnly && c.Data is not null)
        {
            return new CommandLine { Error = "--install-only can't be used with --data." };
        }

        return c;
    }
}
