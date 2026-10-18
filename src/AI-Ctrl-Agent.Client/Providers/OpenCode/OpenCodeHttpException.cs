using System.Net;

namespace AiCtrl.Client.Providers.OpenCode;

public sealed class OpenCodeHttpException : Exception
{
    public OpenCodeHttpException(HttpStatusCode statusCode, string? body, string? message = null)
        : base(message ?? $"OpenCode HTTP {(int)statusCode} ({statusCode}).")
    {
        StatusCode = statusCode;
        Body = body;
    }

    public HttpStatusCode StatusCode { get; }

    public string? Body { get; }
}
