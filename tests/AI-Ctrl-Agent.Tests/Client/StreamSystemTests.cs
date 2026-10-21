using System.Text.Json;
using AiCtrl.Client.Systems;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Providers;
using AiCtrl.Core.Runtime;
using AiCtrl.Tests.Systems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace AiCtrl.Tests.Client;

public sealed class StreamSystemTests
{
    private const string SessionId = "ses_1";

    private const string MessageId = "msg_a1";

    private const string PartId = "prt_a1";

    [Fact]
    public async Task Assistant_text_is_streamed_as_replace_then_append_and_done()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, "Hel", ended: false);
        harness.Text(SessionId, MessageId, PartId, "Hello", ended: false);
        harness.Text(SessionId, MessageId, PartId, "Hello world", ended: true);
        harness.Text(SessionId, MessageId, PartId, "Hello world", ended: true);

        (await harness.Stream.FlushAsync(ct)).ShouldBe(1);

        var batch = harness.Sent.ShouldHaveSingleItem();
        batch.SessionId.ShouldBe(SessionId);
        batch.Epoch.ShouldBeGreaterThan(0);
        batch.Chunks.Select(chunk => chunk.Op)
            .ShouldBe(new[] { StreamOp.Replace, StreamOp.Append, StreamOp.Append, StreamOp.PartDone });
        batch.Chunks.Select(chunk => chunk.Text)
            .ShouldBe(new[] { "Hel", "lo", " world", null });
        batch.Chunks.Select(chunk => chunk.Seq).ShouldBe(new long[] { 1, 2, 3, 4 });
        batch.Chunks.ShouldAllBe(chunk => chunk.PartType == "text");
    }

    [Fact]
    public async Task Assistant_text_deltas_stream_without_relying_on_full_updates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, string.Empty, ended: false);
        harness.Delta(SessionId, MessageId, PartId, "Hel");
        harness.Delta(SessionId, MessageId, PartId, "lo");
        harness.Text(SessionId, MessageId, PartId, "Hello", ended: true);

        (await harness.Stream.FlushAsync(ct)).ShouldBe(1);

        var chunks = harness.Sent.ShouldHaveSingleItem().Chunks;
        chunks.Select(chunk => chunk.Op)
            .ShouldBe(new[] { StreamOp.Replace, StreamOp.Append, StreamOp.PartDone });
        chunks.Select(chunk => chunk.Text).ShouldBe(new[] { "Hel", "lo", null });
    }

    [Fact]
    public async Task Delta_already_included_in_a_full_update_is_not_duplicated()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, "Hel", ended: false);
        harness.Delta(SessionId, MessageId, PartId, "Hel");
        harness.Delta(SessionId, MessageId, PartId, "lo");
        harness.Text(SessionId, MessageId, PartId, "Hello", ended: true);

        (await harness.Stream.FlushAsync(ct)).ShouldBe(1);

        var chunks = harness.Sent.ShouldHaveSingleItem().Chunks;
        chunks.Select(chunk => chunk.Op)
            .ShouldBe(new[] { StreamOp.Replace, StreamOp.Append, StreamOp.PartDone });
        chunks.Select(chunk => chunk.Text).ShouldBe(new[] { "Hel", "lo", null });
    }

    [Fact]
    public async Task User_text_deltas_are_not_streamed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        harness.Message(SessionId, MessageId, "user");
        harness.Text(SessionId, MessageId, PartId, string.Empty, ended: false);
        harness.Delta(SessionId, MessageId, PartId, "prompt");
        harness.Text(SessionId, MessageId, PartId, "prompt", ended: true);

        (await harness.Stream.FlushAsync(ct)).ShouldBe(0);
        harness.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task User_text_and_unwatched_sessions_are_not_streamed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);

        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, "ignored", ended: true);
        (await harness.Stream.FlushAsync(ct)).ShouldBe(0);

        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);
        harness.Message(SessionId, MessageId, "user");
        harness.Text(SessionId, MessageId, PartId, "prompt", ended: true);
        (await harness.Stream.FlushAsync(ct)).ShouldBe(0);

        harness.Stream.SetWatchedSessions(new[] { Ref(new MachineId("m_other")) }, 60);
        harness.Text(SessionId, MessageId, PartId, "other", ended: true);
        (await harness.Stream.FlushAsync(ct)).ShouldBe(0);

        harness.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Text_with_unknown_role_is_streamed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        harness.Text(SessionId, MessageId, PartId, "late attach", ended: true);

        (await harness.Stream.FlushAsync(ct)).ShouldBe(1);
        var batch = harness.Sent.ShouldHaveSingleItem();
        batch.Chunks.Select(chunk => chunk.Op)
            .ShouldBe(new[] { StreamOp.Replace, StreamOp.PartDone });
    }

    [Fact]
    public async Task Rewatch_after_unwatch_starts_a_new_epoch_and_resets_sequences()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, "first", ended: true);
        await harness.Stream.FlushAsync(ct);
        var first = harness.Sent.ShouldHaveSingleItem();

        harness.Stream.SetWatchedSessions(Array.Empty<SessionRef>(), 60);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);
        harness.Text(SessionId, MessageId, PartId, "second", ended: true);
        await harness.Stream.FlushAsync(ct);

        harness.Sent.Count.ShouldBe(2);
        var second = harness.Sent[1];
        second.Epoch.ShouldBeGreaterThan(first.Epoch);
        second.Chunks[0].Op.ShouldBe(StreamOp.Replace);
        second.Chunks[0].Text.ShouldBe("second");
        second.Chunks[0].Seq.ShouldBe(1);
    }

    [Fact]
    public async Task Lease_expiry_stops_streaming_until_the_next_watch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 1);

        harness.Time.Advance(TimeSpan.FromSeconds(2));
        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, "expired", ended: true);
        (await harness.Stream.FlushAsync(ct)).ShouldBe(0);

        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);
        harness.Text(SessionId, MessageId, PartId, "renewed", ended: true);
        (await harness.Stream.FlushAsync(ct)).ShouldBe(1);
        harness.Sent.ShouldHaveSingleItem().Chunks[0].Text.ShouldBe("renewed");
    }

    [Fact]
    public async Task Clear_watches_discards_pending_chunks_and_keeps_the_epoch_increasing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, "pending", ended: true);
        harness.Stream.ClearWatches();
        (await harness.Stream.FlushAsync(ct)).ShouldBe(0);

        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);
        harness.Text(SessionId, MessageId, PartId, "after", ended: true);
        (await harness.Stream.FlushAsync(ct)).ShouldBe(1);
        harness.Sent.ShouldHaveSingleItem().Chunks[0].Op.ShouldBe(StreamOp.Replace);
    }

    [Fact]
    public async Task Long_text_is_split_into_bounded_chunks_and_reassembled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        var text = new string('a', 9000);
        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, text, ended: true);
        (await harness.Stream.FlushAsync(ct)).ShouldBe(1);

        var chunks = harness.Sent.ShouldHaveSingleItem().Chunks;
        chunks.Count.ShouldBe(4);
        chunks[0].Op.ShouldBe(StreamOp.Replace);
        chunks[1].Op.ShouldBe(StreamOp.Append);
        chunks[2].Op.ShouldBe(StreamOp.Append);
        chunks[3].Op.ShouldBe(StreamOp.PartDone);
        chunks.Take(3).ShouldAllBe(chunk => chunk.Text!.Length <= 4000);
        string.Concat(chunks.Take(3).Select(chunk => chunk.Text)).ShouldBe(text);
    }

    [Fact]
    public async Task Tool_state_is_emitted_on_change_and_part_done_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine) }, 60);

        harness.Message(SessionId, MessageId, "assistant");
        harness.Tool(SessionId, MessageId, PartId, "running", "one", completed: false);
        await harness.Stream.FlushAsync(ct);
        harness.Sent.ShouldHaveSingleItem().Chunks.ShouldHaveSingleItem().Op.ShouldBe(StreamOp.ToolState);

        harness.Tool(SessionId, MessageId, PartId, "running", "one", completed: true);
        await harness.Stream.FlushAsync(ct);
        harness.Sent.Count.ShouldBe(2);
        harness.Sent[1].Chunks.ShouldHaveSingleItem().Op.ShouldBe(StreamOp.PartDone);

        harness.Tool(SessionId, MessageId, PartId, "running", "one", completed: true);
        (await harness.Stream.FlushAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Two_watched_sessions_are_flushed_as_separate_batches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(ct);
        harness.Stream.SetWatchedSessions(new[] { Ref(Machine), new SessionRef(Machine, default, "ses_2") }, 60);

        harness.Message(SessionId, MessageId, "assistant");
        harness.Text(SessionId, MessageId, PartId, "one", ended: true);
        harness.Message("ses_2", "msg_b1", "assistant");
        harness.Text("ses_2", "msg_b1", "prt_b1", "two", ended: true);

        (await harness.Stream.FlushAsync(ct)).ShouldBe(2);
        harness.Sent.Select(batch => batch.SessionId).ShouldBe(new[] { SessionId, "ses_2" });
        harness.Sent.ShouldAllBe(batch => batch.Chunks.Count == 2);
    }

    private static readonly MachineId Machine = new("m_test");

    private static SessionRef Ref(MachineId machine) => new(machine, default, SessionId);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly AcLoopSupervisor _loops;

        private readonly ServiceProvider _services;

        private readonly ProviderEventBroker _events;

        private readonly List<StreamBatch> _sent = new();

        private Harness(
            FakeProviderInstance instance,
            FakeTimeProvider time,
            StreamSystem stream,
            AcLoopSupervisor loops,
            ServiceProvider services,
            ProviderEventBroker events)
        {
            Instance = instance;
            Time = time;
            Stream = stream;
            _loops = loops;
            _services = services;
            _events = events;
        }

        public FakeProviderInstance Instance { get; }

        public FakeTimeProvider Time { get; }

        public StreamSystem Stream { get; }

        public IReadOnlyList<StreamBatch> Sent
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToList();
                }
            }
        }

        public static async Task<Harness> StartAsync(CancellationToken ct)
        {
            var time = new FakeTimeProvider();
            var logs = NullLoggerFactory.Instance;
            var instance = new FakeProviderInstance();
            var registry = new FakeProviderInstanceRegistry(Machine, new[] { instance });
            var events = new ProviderEventBroker(NullLogger<ProviderEventBroker>.Instance);
            var push = new AgentPushChannel(NullLogger<AgentPushChannel>.Instance);
            var stream = new StreamSystem(registry, events, push, time, logs);
            var loops = new AcLoopSupervisor(time, logs);
            var services = new ServiceCollection().BuildServiceProvider();
            var ctx = new AcSystemContext(services, new ChannelAcEventBus(logs), time, logs, loops);

            await stream.InitAsync(ctx, ct);

            var harness = new Harness(instance, time, stream, loops, services, events);
            push.Attach((batch, _) =>
            {
                lock (harness._sent)
                {
                    harness._sent.Add(batch);
                }

                return Task.CompletedTask;
            });
            return harness;
        }

        public void Message(string sessionId, string messageId, string role) =>
            Publish("message.updated", new Dictionary<string, object?>
            {
                ["sessionID"] = sessionId,
                ["info"] = new Dictionary<string, object?>
                {
                    ["id"] = messageId,
                    ["role"] = role,
                },
            });

        public void Text(string sessionId, string messageId, string partId, string text, bool ended) =>
            Publish("message.part.updated", new Dictionary<string, object?>
            {
                ["sessionID"] = sessionId,
                ["part"] = new Dictionary<string, object?>
                {
                    ["id"] = partId,
                    ["messageID"] = messageId,
                    ["type"] = "text",
                    ["text"] = text,
                    ["time"] = PartTime(ended),
                },
            });

        public void Delta(string sessionId, string messageId, string partId, string delta) =>
            Publish("message.part.delta", new Dictionary<string, object?>
            {
                ["sessionID"] = sessionId,
                ["messageID"] = messageId,
                ["partID"] = partId,
                ["field"] = "text",
                ["delta"] = delta,
            });

        public void Tool(
            string sessionId,
            string messageId,
            string partId,
            string status,
            string output,
            bool completed) =>
            Publish("message.part.updated", new Dictionary<string, object?>
            {
                ["sessionID"] = sessionId,
                ["part"] = new Dictionary<string, object?>
                {
                    ["id"] = partId,
                    ["messageID"] = messageId,
                    ["type"] = "tool",
                    ["tool"] = "bash",
                    ["state"] = new Dictionary<string, object?>
                    {
                        ["status"] = status,
                        ["title"] = "Run command",
                        ["output"] = output,
                        ["input"] = "ls",
                        ["time"] = PartTime(completed),
                    },
                },
            });

        public async ValueTask DisposeAsync()
        {
            await Stream.StopAsync(CancellationToken.None);
            await _loops.DisposeAsync();
            await Instance.DisposeAsync();
            await _services.DisposeAsync();
        }

        private static Dictionary<string, object?> PartTime(bool ended) =>
            ended
                ? new Dictionary<string, object?> { ["start"] = 1000L, ["end"] = 2000L }
                : new Dictionary<string, object?> { ["start"] = 1000L };

        private void Publish(string kind, Dictionary<string, object?> properties) =>
            _events.Publish(
                Instance,
                new ProviderEvent(kind, JsonSerializer.SerializeToElement(properties)));
    }
}
