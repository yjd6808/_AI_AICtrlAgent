using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCtrl.FakeOpenCode;

public sealed class FakeOpenCodeServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebApplication _app;

    private FakeOpenCodeServer(WebApplication app, FakeOpenCodeState state, FakeOpenCodeScenario scenario)
    {
        _app = app;
        State = state;
        Scenario = scenario;
    }

    public FakeOpenCodeScenario Scenario { get; }

    public FakeOpenCodeState State { get; }

    public string BaseUrl { get; private set; } = string.Empty;

    public static async Task<FakeOpenCodeServer> StartAsync(Action<FakeOpenCodeScenario>? configure = null, CancellationToken ct = default, string? url = null)
    {
        var scenario = new FakeOpenCodeScenario();
        configure?.Invoke(scenario);
        var hub = new FakeSseHub();
        var state = new FakeOpenCodeState(scenario, hub);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url ?? "http://127.0.0.1:0");
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (!IsAuthorized(context.Request, scenario))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"opencode\"";
                return;
            }

            await next();
        });

        app.MapGet("/global/health", () => Results.Json(new Dictionary<string, object?>
        {
            ["healthy"] = true,
            ["version"] = "1.18.34",
        }, Json));

        app.MapGet("/doc", () => Results.Text(scenario.DocJson ?? BuildDocJson(), "application/json"));

        app.MapGet("/session", () => Results.Json(state.ListSessions(), Json));

        app.MapGet("/session/status", () => Results.Json(state.BuildStatusMap(), Json));

        app.MapGet("/session/{sessionID}", (string sessionID) =>
            state.GetSession(sessionID) is { } session
                ? Results.Json(session, Json)
                : NotFound());

        app.MapGet("/session/{sessionID}/message", (string sessionID) =>
            state.SessionExists(sessionID)
                ? Results.Json(state.GetMessages(sessionID), Json)
                : NotFound());

        app.MapGet("/session/{sessionID}/children", (string sessionID) =>
            state.SessionExists(sessionID)
                ? Results.Json(state.ListChildren(sessionID), Json)
                : NotFound());

        app.MapPost("/session/{sessionID}/prompt_async", async (HttpContext context, string sessionID) =>
        {
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                var messageId = ReadString(root, "messageID");
                var agent = ReadString(root, "agent");
                var model = ReadModel(root);
                var text = ExtractText(root);

                if (messageId is not null && !messageId.StartsWith("msg", StringComparison.Ordinal))
                {
                    await Results.Json(
                            new
                            {
                                name = "BadRequest",
                                data = new
                                {
                                    message = $"Expected a string starting with \"msg\", got \"{messageId}\"",
                                    kind = "Payload",
                                },
                            },
                            statusCode: StatusCodes.Status400BadRequest)
                        .ExecuteAsync(context);
                    return;
                }

                if (scenario.PromptAcceptDelay > TimeSpan.Zero)
                {
                    await Task.Delay(scenario.PromptAcceptDelay, context.RequestAborted);
                }

                if (!state.AcceptPrompt(sessionID, messageId, text, agent, model))
                {
                    await NotFound().ExecuteAsync(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
        });

        app.MapPost("/session/{sessionID}/abort", (string sessionID) =>
            Results.Json(state.Abort(sessionID), Json));

        app.MapPost("/session/{sessionID}/permissions/{permissionID}", async (HttpContext context, string sessionID, string permissionID) =>
        {
            var response = "once";
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var parsed = ReadString(document.RootElement, "response");
                if (parsed is not null)
                {
                    response = parsed;
                }
            }
            catch (JsonException)
            {
            }

            state.RecordPermissionReply(permissionID, response);
            return Results.Json(true, Json);
        });

        app.MapGet("/event", async (HttpContext context) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";

            var (reader, lease) = hub.Subscribe();
            try
            {
                hub.Publish("server.connected", new Dictionary<string, object?>());
                var sent = 0;
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    string json;
                    try
                    {
                        json = await reader.ReadAsync(context.RequestAborted);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }

                    await context.Response.WriteAsync("data: " + json + "\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    sent++;

                    if (scenario.ShouldDropSse(sent))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lease.Dispose();
            }
        });

        app.MapGet("/config/providers", () => Results.Json(new Dictionary<string, object?>
        {
            ["providers"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "opencode",
                    ["name"] = "opencode",
                    ["models"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = "fake-model",
                            ["name"] = "Fake Model",
                        },
                    },
                },
            },
        }, Json));

        await app.StartAsync(ct).ConfigureAwait(false);
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        return new FakeOpenCodeServer(app, state, scenario)
        {
            BaseUrl = (addresses?.FirstOrDefault() ?? "http://127.0.0.1").TrimEnd('/'),
        };
    }

    public async ValueTask DisposeAsync()
    {
        State.Shutdown();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static IResult NotFound() => Results.Json(new Dictionary<string, object?>
    {
        ["name"] = "NotFoundError",
        ["data"] = new Dictionary<string, object?> { ["message"] = "Session not found" },
    }, Json, statusCode: StatusCodes.Status404NotFound);

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static FakeModelDto? ReadModel(JsonElement root)
    {
        if (!root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = ReadString(model, "modelID") ?? ReadString(model, "id");
        var providerId = ReadString(model, "providerID") ?? "opencode";
        return id is null ? null : new FakeModelDto(id, providerId);
    }

    private static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = ReadString(part, "type");
                if (type is not null && type != "text")
                {
                    continue;
                }

                var text = ReadString(part, "text");
                if (text is not null)
                {
                    builder.Append(text);
                }
            }

            return builder.ToString();
        }

        return ReadString(root, "text") ?? string.Empty;
    }

    private static bool IsAuthorized(HttpRequest request, FakeOpenCodeScenario scenario)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        var user = decoded[..separator];
        var password = decoded[(separator + 1)..];
        return string.Equals(user, scenario.Username, StringComparison.Ordinal) &&
               string.Equals(password, scenario.Password, StringComparison.Ordinal);
    }

    private static string BuildDocJson()
    {
        var paths = new Dictionary<string, object?>
        {
            ["/global/health"] = Operation("get"),
            ["/doc"] = Operation("get"),
            ["/session"] = Operation("get"),
            ["/session/status"] = Operation("get"),
            ["/session/{sessionID}"] = Operation("get"),
            ["/session/{sessionID}/message"] = Operation("get"),
            ["/session/{sessionID}/children"] = Operation("get"),
            ["/session/{sessionID}/prompt_async"] = Operation("post"),
            ["/session/{sessionID}/abort"] = Operation("post"),
            ["/session/{sessionID}/permissions/{permissionID}"] = Operation("post"),
            ["/event"] = Operation("get"),
            ["/config/providers"] = Operation("get"),
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = "opencode",
                ["version"] = "1.0.0",
            },
            ["paths"] = paths,
        }, Json);
    }

    private static Dictionary<string, object?> Operation(string method) => new()
    {
        [method] = new Dictionary<string, object?>
        {
            ["responses"] = new Dictionary<string, object?>(),
        },
    };
}
