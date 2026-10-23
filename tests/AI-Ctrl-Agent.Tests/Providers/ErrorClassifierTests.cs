using AiCtrl.Client.Diagnostics;
using AiCtrl.Core.Domain;
using Shouldly;

namespace AiCtrl.Tests.Providers;

public sealed class ErrorClassifierTests
{
    private readonly ErrorClassifier _classifier = new();

    [Theory]
    [InlineData("MessageAbortedError", "Aborted", null, ErrorClass.Aborted)]
    [InlineData("UnknownError", "quota exhausted", 402, ErrorClass.QuotaExceeded)]
    [InlineData("UnknownError", "Free usage limit reached", null, ErrorClass.QuotaExceeded)]
    [InlineData("UnknownError", "Free usage exceeded. Subscribe to Go for more.", null, ErrorClass.QuotaExceeded)]
    [InlineData("UnknownError", "rate limit exceeded", 429, ErrorClass.RateLimited)]
    [InlineData("UnknownError", "too many requests", null, ErrorClass.RateLimited)]
    [InlineData("UnknownError", "unauthorized", 401, ErrorClass.AuthFailed)]
    [InlineData("UnknownError", "forbidden", 403, ErrorClass.AuthFailed)]
    [InlineData("UnknownError", "model glm-4.7 unavailable", 404, ErrorClass.ModelUnavailable)]
    [InlineData("UnknownError", "model overloaded", 503, ErrorClass.ModelUnavailable)]
    [InlineData("UnknownError", "fetch failed", null, ErrorClass.Network)]
    [InlineData("UnknownError", "connect ECONNREFUSED 127.0.0.1:4096", null, ErrorClass.Network)]
    [InlineData("UnknownError", "request timed out after 30s", null, ErrorClass.Network)]
    [InlineData("UnknownError", "something odd", null, ErrorClass.Unknown)]
    public void Classify_maps_error_signals(string name, string message, int? statusCode, ErrorClass expected)
    {
        _classifier.Classify(name, message, statusCode).Class.ShouldBe(expected);
    }

    [Fact]
    public void Classify_requires_status_and_message_for_model_unavailable()
    {
        _classifier.Classify("UnknownError", "session not found", 404).Class.ShouldBe(ErrorClass.Unknown);
        _classifier.Classify("UnknownError", "model unavailable", null).Class.ShouldBe(ErrorClass.Unknown);
        _classifier.Classify("UnknownError", "model unavailable", 404).Class.ShouldBe(ErrorClass.ModelUnavailable);
    }

    [Fact]
    public void Classify_keeps_raw_json_and_original_fields()
    {
        var info = _classifier.Classify("APIError", "quota exceeded", 402, "{\"raw\":true}");

        info.Name.ShouldBe("APIError");
        info.Message.ShouldBe("quota exceeded");
        info.StatusCode.ShouldBe(402);
        info.RawJson.ShouldBe("{\"raw\":true}");
    }

    [Theory]
    [InlineData("quota", "Free usage limit reached", ErrorClass.QuotaExceeded)]
    [InlineData("rate-limit", null, ErrorClass.RateLimited)]
    [InlineData(null, "fetch failed", ErrorClass.Network)]
    [InlineData(null, null, ErrorClass.Unknown)]
    public void Classify_retry_combines_reason_and_message(string? reason, string? message, ErrorClass expected)
    {
        _classifier.ClassifyRetry(reason, message).Class.ShouldBe(expected);
    }

    [Fact]
    public void Classify_retry_names_unknown_errors_as_retry()
    {
        _classifier.ClassifyRetry(null, "odd retry message").Name.ShouldBe("Retry");
    }

    [Fact]
    public void Custom_rules_take_precedence_over_defaults()
    {
        var custom = new ErrorClassifier(new[]
        {
            new ErrorRule(ErrorClass.ToolError, null, "^ToolExploded$", null),
        });

        custom.Classify("ToolExploded", "boom", null).Class.ShouldBe(ErrorClass.ToolError);
        custom.Classify("MessageAbortedError", "Aborted", null).Class.ShouldBe(ErrorClass.Unknown);
    }
}
