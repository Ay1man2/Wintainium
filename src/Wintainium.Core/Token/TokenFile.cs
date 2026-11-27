using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Wintainium.Core.Token;

/// <summary>A saved GitHub token and when it expires (local time), if GitHub said.</summary>
public sealed record StoredToken(string Token, DateTime? Expires);

/// <summary>
/// wintainium-token.xml, in the format v0.3.0 wrote with Export-Clixml: a PSCustomObject with
/// Token (a SecureString: DPAPI-encrypted UTF-16 text as hex) and Expires (a date or Nil).
/// Reading it lets people keep their token; writing the same format keeps both versions compatible.
/// </summary>
public static class TokenFile
{
    private static readonly XNamespace Ps = "http://schemas.microsoft.com/powershell/2004/04";

    /// <summary>Reads the token. Throws <see cref="TokenFileException"/> when the file can't be used.</summary>
    public static StoredToken Read(string path, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(path, settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new TokenFileException($"Could not read {Path.GetFileName(path)}: {ex.Message}", ex);
        }

        // Look anywhere in the document: the layout around the properties differs a little between
        // PowerShell versions, but the Token (SS) and Expires (DT) elements are always named.
        XElement? ss = doc.Descendants(Ps + "SS").FirstOrDefault(e => (string?)e.Attribute("N") == "Token");
        if (ss is null || string.IsNullOrWhiteSpace(ss.Value))
        {
            throw new TokenFileException($"{Path.GetFileName(path)} holds no token.");
        }

        string token;
        try
        {
            byte[] plain = protector.Unprotect(Convert.FromHexString(ss.Value.Trim()));
            try
            {
                token = Encoding.Unicode.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or PlatformNotSupportedException)
        {
            // DPAPI only decrypts for the Windows user who saved it, on the same PC
            throw new TokenFileException($"Could not decrypt the token in {Path.GetFileName(path)}: {ex.Message}", ex);
        }

        DateTime? expires = null;
        XElement? dt = doc.Descendants(Ps + "DT").FirstOrDefault(e => (string?)e.Attribute("N") == "Expires");
        if (dt is not null)
        {
            try
            {
                expires = XmlConvert.ToDateTime(dt.Value.Trim(), XmlDateTimeSerializationMode.Local);
            }
            catch (FormatException)
            {
                expires = null;
            }
        }

        return new StoredToken(token, expires);
    }

    /// <summary>Writes the token in Export-Clixml's format (UTF-16 with BOM, as PowerShell writes it).</summary>
    public static void Write(string path, StoredToken token, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(protector);
        byte[] plain = Encoding.Unicode.GetBytes(token.Token.Trim());
        string hex;
        try
        {
            hex = Convert.ToHexStringLower(protector.Protect(plain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        XElement expires = token.Expires is DateTime e
            ? new XElement(Ps + "DT", new XAttribute("N", "Expires"), XmlConvert.ToString(e, XmlDateTimeSerializationMode.RoundtripKind))
            : new XElement(Ps + "Nil", new XAttribute("N", "Expires"));

        var doc = new XDocument(
            new XElement(
                Ps + "Objs",
                new XAttribute("Version", "1.1.0.1"),
                new XElement(
                    Ps + "Obj",
                    new XAttribute("RefId", "0"),
                    new XElement(
                        Ps + "TN",
                        new XAttribute("RefId", "0"),
                        new XElement(Ps + "T", "System.Management.Automation.PSCustomObject"),
                        new XElement(Ps + "T", "System.Object")),
                    new XElement(
                        Ps + "MS",
                        new XElement(Ps + "SS", new XAttribute("N", "Token"), hex),
                        expires))));

        string full = Path.GetFullPath(path);
        string temp = full + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            var settings = new XmlWriterSettings { Encoding = new UnicodeEncoding(false, true), Indent = true };
            using (XmlWriter writer = XmlWriter.Create(temp, settings))
            {
                doc.Save(writer);
            }

            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

public sealed class TokenFileException : Exception
{
    public TokenFileException()
    {
    }

    public TokenFileException(string message)
        : base(message)
    {
    }

    public TokenFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
