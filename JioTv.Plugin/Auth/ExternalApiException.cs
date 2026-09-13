using System;

namespace JioTv.Plugin.Auth;

/// <summary>
/// External Jio API failure (bad status, malformed payload).
/// </summary>
public class ExternalApiException : Exception
{
    public ExternalApiException(string message)
        : base(message)
    {
    }
}
