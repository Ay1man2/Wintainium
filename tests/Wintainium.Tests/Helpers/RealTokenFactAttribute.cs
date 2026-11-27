namespace Wintainium.Tests.Helpers;

/// <summary>
/// A test that reads the owner's real token file, which only decrypts on their own Windows account.
/// It runs only when the WINTAINIUM_REAL_TOKEN_FILE environment variable names that file, and is
/// skipped everywhere else, including CI.
/// </summary>
public sealed class RealTokenFactAttribute : FactAttribute
{
    public const string Variable = "WINTAINIUM_REAL_TOKEN_FILE";

    public RealTokenFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a copy of a token file saved by v0.3.0 to run this";
        }
    }
}
