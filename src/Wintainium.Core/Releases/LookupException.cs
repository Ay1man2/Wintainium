using System.Net;

namespace Wintainium.Core.Releases;

/// <summary>A release lookup that failed: the HTTP status (when there was one), the server's text, and the kind of failure.</summary>
public sealed class LookupException : Exception
{
    public LookupException()
    {
    }

    public LookupException(string message)
        : base(message)
    {
    }

    public LookupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LookupException(string message, HttpStatusCode? status, string body, bool unreachable, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Body = body;
        Unreachable = unreachable;
    }

    public HttpStatusCode? Status { get; }

    /// <summary>The response text (GitHub puts "Bad credentials" or "rate limit" here).</summary>
    public string Body { get; } = "";

    /// <summary>The site couldn't be reached at all: timeout, name lookup or connection failure.</summary>
    public bool Unreachable { get; }
}
