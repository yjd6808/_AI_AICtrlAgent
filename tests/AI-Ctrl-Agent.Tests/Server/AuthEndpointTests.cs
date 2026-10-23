using System.Net;
using System.Text.Json;
using AiCtrl.Core.Api;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace AiCtrl.Tests.Server;

public sealed class AuthEndpointTests
{
    [Fact]
    public async Task Api_returns_401_problem_for_unauthenticated_requests()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = TestServerHost.CreateDirectory();
        try
        {
            await using var host = await TestServerHost.StartAsync(directory);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(host.BaseUrl),
            };

            foreach (var path in new[] { "/api/v1/auth/me", "/api/v1/machines" })
            {
                using var response = await client.GetAsync(path, ct);
                response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
                response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", path);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                document.RootElement.GetProperty("code").GetString().ShouldBe(AcErrorCodes.Unauthorized, path);
                document.RootElement.GetProperty("status").GetInt32().ShouldBe((int)HttpStatusCode.Unauthorized, path);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Login_returns_503_when_google_is_not_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = TestServerHost.CreateDirectory();
        try
        {
            await using var host = await TestServerHost.StartAsync(directory);
            using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
            using var response = await client.GetAsync("/api/v1/auth/login", ct);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            document.RootElement.GetProperty("code").GetString().ShouldBe(AcErrorCodes.AuthUnavailable);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
