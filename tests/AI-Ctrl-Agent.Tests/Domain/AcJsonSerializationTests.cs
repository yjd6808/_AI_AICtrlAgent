using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Serialization;
using Shouldly;
using Xunit;

namespace AiCtrl.Tests.Domain;

public sealed class AcJsonSerializationTests
{
    [Fact]
    public void RequestPacket_round_trips_with_typed_payload()
    {
        var payload = JsonSerializer.SerializeToElement(
            new SessionPromptPayload(
                new SessionRef(new MachineId("m_01"), new InstanceId("opencode:127.0.0.1:4096"), "ses_abc"),
                "continue",
                new ModelRef("opencode", "deepseek-v4.1-flash-free"),
                null),
            AcJsonContext.Default.SessionPromptPayload);

        var packet = new RequestPacket(
            "01JB",
            RequestKind.Command,
            CommandTypes.SessionPrompt,
            payload,
            "4bf92f",
            "user:me@example.com",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            1);

        var json = JsonSerializer.Serialize(packet, AcJsonContext.Default.RequestPacket);
        json.ShouldContain("\"requestId\":\"01JB\"");
        json.ShouldContain("\"kind\":\"Command\"");
        json.ShouldContain("\"sessionId\":\"ses_abc\"");

        var roundTripped = JsonSerializer.Deserialize(json, AcJsonContext.Default.RequestPacket);
        roundTripped.ShouldNotBeNull();
        roundTripped.RequestId.ShouldBe("01JB");
        roundTripped.Kind.ShouldBe(RequestKind.Command);
        roundTripped.Type.ShouldBe(CommandTypes.SessionPrompt);
    }

    [Fact]
    public void ResponseStatus_serializes_as_string()
    {
        var packet = new ResponsePacket(
            "01JB",
            ResponseStatus.Succeeded,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch,
            184);

        var json = JsonSerializer.Serialize(packet, AcJsonContext.Default.ResponsePacket);

        json.ShouldContain("\"status\":\"Succeeded\"");
        json.ShouldContain("\"elapsedMs\":184");
    }

    [Fact]
    public void MachineId_round_trips_as_single_value_object()
    {
        var id = new MachineId("m_01J");

        var json = JsonSerializer.Serialize(id, AcJsonContext.Default.MachineId);
        json.ShouldBe("{\"value\":\"m_01J\"}");

        JsonSerializer.Deserialize(json, AcJsonContext.Default.MachineId).ShouldBe(id);
    }

    [Fact]
    public void ApiResponse_envelope_round_trips_with_meta()
    {
        var response = new ApiResponse<SessionSummary>(
            new SessionSummary(
                new SessionRef(new MachineId("m_01"), new InstanceId("opencode:127.0.0.1:4096"), "ses_abc"),
                "task", null, @"D:\Work\mabinogi-tools",
                AcRunState.Busy,
                ErrorClass.None,
                null,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                SessionDisplayState.Running,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                12,
                true),
            new ResponseMeta("live", DateTimeOffset.UnixEpoch, true, false, 184, "4bf92f"));

        var json = JsonSerializer.Serialize(response, AcJsonContext.Default.ApiResponseSessionSummary);
        json.ShouldContain("\"displayState\":\"Running\"");
        json.ShouldContain("\"source\":\"live\"");
        json.ShouldContain("\"stale\":false");

        var roundTripped = JsonSerializer.Deserialize(json, AcJsonContext.Default.ApiResponseSessionSummary);
        roundTripped.ShouldNotBeNull();
        roundTripped.Data.Ref.SessionId.ShouldBe("ses_abc");
        roundTripped.Meta.TraceId.ShouldBe("4bf92f");
    }

    [Fact]
    public void StreamBatch_round_trips_with_ops_as_strings()
    {
        var batch = new StreamBatch(
            new MachineId("m_01"),
            "ses_abc",
            3,
            [
                new StreamChunk("msg_1", "prt_1", "text", 7, StreamOp.Append, "partial", null),
                new StreamChunk("msg_1", "prt_2", "tool", 8, StreamOp.ToolState, null,
                    new ToolPartView("bash", "completed", "npm test", "ok", "npm test", null, null)),
            ]);

        var json = JsonSerializer.Serialize(batch, AcJsonContext.Default.StreamBatch);
        json.ShouldContain("\"op\":\"Append\"");
        json.ShouldContain("\"op\":\"ToolState\"");

        var roundTripped = JsonSerializer.Deserialize(json, AcJsonContext.Default.StreamBatch);
        roundTripped.ShouldNotBeNull();
        roundTripped.Chunks.Count.ShouldBe(2);
        roundTripped.Chunks[1].Tool!.Status.ShouldBe("completed");
    }

    [Fact]
    public void MachineInfo_round_trips_with_nested_views()
    {
        var machine = new MachineInfo(
            new MachineId("m_01"),
            "desk", "Windows 11", "0.1.0",
            true,
            DateTimeOffset.UnixEpoch,
            "203.0.113.7",
            "KR",
            ObservedIpSources.Connection,
            null,
            [
                new InstanceInfo(
                    new InstanceId("opencode:127.0.0.1:4096"),
                    ProviderKind.OpenCode,
                    "http://127.0.0.1:4096",
                    "1.2.3",
                    InstanceMode.Managed,
                    true,
                    true,
                    CapabilitySet.ListSessions | CapabilitySet.Prompt),
            ],
            [
                new WorkspaceInfo(
                    "ws_a1b2c3d4", "mabinogi-tools", @"D:\Work\mabinogi-tools",
                    true, true, true, false, [], []),
            ],
            null,
            false,
            [
                new MachineFeature(
                    "agent:opencode:127.0.0.1:4096",
                    FeatureSection.Agents,
                    "OpenCode :4096",
                    "/m/m_01/agents/opencode:127.0.0.1:4096",
                    true,
                    null,
                    "읽기 전용"),
            ]);

        var json = JsonSerializer.Serialize(machine, AcJsonContext.Default.MachineInfo);
        json.ShouldContain("\"kind\":\"OpenCode\"");
        json.ShouldContain("\"mode\":\"Managed\"");
        json.ShouldContain("\"section\":\"Agents\"");
        json.ShouldContain("\"capabilities\":");

        var roundTripped = JsonSerializer.Deserialize(json, AcJsonContext.Default.MachineInfo);
        roundTripped.ShouldNotBeNull();
        roundTripped.ObservedIpSource.ShouldBe("connection");
        roundTripped.Instances[0].Capabilities.ShouldBe(CapabilitySet.ListSessions | CapabilitySet.Prompt);
        roundTripped.Workspaces[0].WorkspaceId.ShouldBe("ws_a1b2c3d4");
    }

    [Fact]
    public void CommandEnvelope_round_trips_with_payload_element()
    {
        var payload = JsonSerializer.SerializeToElement(
            new VpnConnectPayload(null, "JP", "Tokyo"),
            AcJsonContext.Default.VpnConnectPayload);

        var envelope = new CommandEnvelope(
            "01JC",
            new MachineId("m_01"),
            CommandTypes.VpnConnect,
            payload,
            "user:me@example.com",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            1);

        var json = JsonSerializer.Serialize(envelope, AcJsonContext.Default.CommandEnvelope);
        json.ShouldContain("\"type\":\"vpn.connect\"");
        json.ShouldContain("\"country\":\"JP\"");

        var roundTripped = JsonSerializer.Deserialize(json, AcJsonContext.Default.CommandEnvelope);
        roundTripped.ShouldNotBeNull();
        roundTripped.Payload.GetProperty("city").GetString().ShouldBe("Tokyo");
    }
}
