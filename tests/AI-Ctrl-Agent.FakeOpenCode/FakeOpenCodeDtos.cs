using System.Text.Json.Serialization;

namespace AiCtrl.FakeOpenCode;

public sealed record FakeModelDto(string Id, [property: JsonPropertyName("providerID")] string ProviderId);

public sealed record FakeCacheDto(long Read, long Write);

public sealed record FakeTokensDto(long Input, long Output, long Reasoning, FakeCacheDto Cache);

public sealed record FakeTimeDto(long Created, long? Updated);

public sealed record FakeSessionDto(
    string Id,
    string Slug,
    [property: JsonPropertyName("projectID")] string ProjectId,
    string Directory,
    [property: JsonPropertyName("parentID")] string? ParentId,
    string Title,
    FakeModelDto Model,
    FakeTimeDto Time,
    string Version);

public sealed record FakeMessageTimeDto(long Created, long? Completed);

public sealed record FakeErrorDto(string Name, Dictionary<string, object?> Data);

public sealed record FakeMessageDto(
    string Id,
    [property: JsonPropertyName("sessionID")] string SessionId,
    string Role,
    FakeMessageTimeDto Time,
    string Agent,
    FakeModelDto? Model,
    [property: JsonPropertyName("parentID")] string? ParentId,
    [property: JsonPropertyName("modelID")] string? ModelId,
    [property: JsonPropertyName("providerID")] string? ProviderId,
    string? Mode,
    string? Path,
    double? Cost,
    FakeTokensDto? Tokens,
    FakeErrorDto? Error,
    string? Finish);

public sealed record FakePartTimeDto(long Start, long? End);

public sealed record FakePartDto(
    string Id,
    [property: JsonPropertyName("sessionID")] string SessionId,
    [property: JsonPropertyName("messageID")] string MessageId,
    string Type,
    string? Text,
    [property: JsonPropertyName("callID")] string? CallId,
    string? Tool,
    Dictionary<string, object?>? State,
    FakePartTimeDto? Time);

public sealed record FakeMessageListEntryDto(FakeMessageDto Info, IReadOnlyList<FakePartDto> Parts);
