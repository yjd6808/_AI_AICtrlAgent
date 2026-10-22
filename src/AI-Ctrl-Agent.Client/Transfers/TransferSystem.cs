using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AiCtrl.Client.Link;
using AiCtrl.Client.Storage;
using AiCtrl.Client.Workspace;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Runtime;
using AiCtrl.Core.Workspace;
using Microsoft.Extensions.Logging;

namespace AiCtrl.Client.Transfers;

public sealed class ClientTransferException : Exception
{
    public ClientTransferException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class TransferSystem : IAcSystem
{
    private readonly ClientDb _db;

    private readonly WorkspaceRegistry _workspaces;

    private readonly TransferLinkState _link;

    private readonly TimeProvider _time;

    private readonly ILogger<TransferSystem> _logger;

    private readonly string _tempDirectory;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;

    private int _stopped;

    public TransferSystem(
        ClientDb db,
        WorkspaceRegistry workspaces,
        TransferLinkState link,
        TimeProvider time,
        ILogger<TransferSystem> logger,
        string? tempDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _workspaces = workspaces;
        _link = link;
        _time = time;
        _logger = logger;
        _tempDirectory = tempDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiCtrl",
            "transfers");
    }

    public string Name => "transfers";

    public IReadOnlyList<string> DependsOn { get; } = ["server-link"];

    public ValueTask InitAsync(AcSystemContext ctx, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(() => ResumeLoopAsync(_cts.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    public async Task<FileMutationResult> ReceiveAsync(FilesReceivePayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var gate = _gates.GetOrAdd(payload.TransferId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (api, token) = Link();
            var status = await api.GetTransferAsync(payload.TransferId, token, ct).ConfigureAwait(false);
            if (status.Direction != TransferDirections.Upload || status.Status != TransferStatuses.Ready)
            {
                throw new ClientTransferException(
                    AcErrorCodes.InvalidPayload,
                    $"Transfer is {status.Direction}/{status.Status}.");
            }

            if (!string.Equals(status.WorkspaceId, payload.WorkspaceId, StringComparison.Ordinal))
            {
                throw new ClientTransferException(AcErrorCodes.WorkspaceNotAllowed, "Workspace does not match the transfer.");
            }

            var entry = _workspaces.Get(payload.WorkspaceId);
            if (!entry.Definition.AllowUpload)
            {
                throw new ClientTransferException(AcErrorCodes.PathDenied, "Uploads are disabled for this workspace.");
            }

            var fileName = string.IsNullOrWhiteSpace(payload.FileName)
                ? status.RelPath.Split('/')[^1]
                : payload.FileName;
            var relative = string.IsNullOrWhiteSpace(payload.RelDir)
                ? fileName
                : payload.RelDir.Replace('\\', '/').TrimEnd('/') + "/" + fileName;
            var destination = entry.Policy.Resolve(relative);
            var parent = Path.GetDirectoryName(destination.FullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                throw new ClientTransferException(AcErrorCodes.PathInvalid, "The destination directory does not exist.");
            }

            await _db.UpsertTransferAsync(
                new ClientTransferRecord(
                    payload.TransferId,
                    TransferDirections.Upload,
                    payload.WorkspaceId,
                    relative,
                    status.Size,
                    status.Sha256,
                    "[]",
                    TransferStatuses.Uploading),
                ct).ConfigureAwait(false);

            Directory.CreateDirectory(_tempDirectory);
            var tempPath = TempPath(payload.TransferId);
            var existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
            if (existing > status.Size)
            {
                File.Delete(tempPath);
                existing = 0;
            }

            if (existing < status.Size)
            {
                await using var stream = new FileStream(
                    tempPath,
                    existing == 0 ? FileMode.Create : FileMode.Append,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await api.CopyTransferContentAsync(payload.TransferId, token, existing, stream, ct)
                    .ConfigureAwait(false);
            }

            var info = new FileInfo(tempPath);
            if (info.Length != status.Size)
            {
                throw new ClientTransferException(
                    AcErrorCodes.InvalidPayload,
                    $"Transfer size is {info.Length} but expected {status.Size}.");
            }

            if (!string.IsNullOrWhiteSpace(status.Sha256))
            {
                var actual = await HashAsync(tempPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, status.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    throw new ClientTransferException(AcErrorCodes.InvalidPayload, "Transfer sha256 mismatch.");
                }
            }

            var finalPath = ResolveConflict(destination.FullPath, payload.Conflict);
            File.Move(tempPath, finalPath, overwrite: true);
            await _db.DeleteTransferAsync(payload.TransferId, ct).ConfigureAwait(false);
            await api.ReleaseTransferAsync(payload.TransferId, token, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Received transfer {TransferId} into {Path}.",
                payload.TransferId,
                finalPath);
            var finalRelative = Path.GetRelativePath(entry.Definition.Root, finalPath).Replace('\\', '/');
            return new FileMutationResult(payload.WorkspaceId, finalRelative, false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FileMutationResult> SendAsync(FilesSendPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var gate = _gates.GetOrAdd(payload.TransferId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        string? zipPath = null;
        try
        {
            var (api, token) = Link();
            var entry = _workspaces.Get(payload.WorkspaceId);
            if (!entry.Definition.AllowDownload)
            {
                throw new ClientTransferException(AcErrorCodes.PathDenied, "Downloads are disabled for this workspace.");
            }

            var source = entry.Policy.Resolve(payload.RelPath);
            string contentPath;
            if (Directory.Exists(source.FullPath))
            {
                if (!payload.AsZip)
                {
                    throw new ClientTransferException(
                        AcErrorCodes.InvalidPayload,
                        "Directory downloads require asZip.");
                }

                Directory.CreateDirectory(_tempDirectory);
                zipPath = Path.Combine(_tempDirectory, payload.TransferId + ".zip");
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }

                ZipFile.CreateFromDirectory(source.FullPath, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
                contentPath = zipPath;
            }
            else if (File.Exists(source.FullPath))
            {
                contentPath = source.FullPath;
            }
            else
            {
                throw new ClientTransferException(AcErrorCodes.PathInvalid, "The download source was not found.");
            }

            var size = new FileInfo(contentPath).Length;
            var sha = await HashAsync(contentPath, ct).ConfigureAwait(false);
            var status = await api
                .BeginTransferAsync(payload.TransferId, new TransferBeginRequest(size, sha), token, ct)
                .ConfigureAwait(false);
            if (status.ChunkSize <= 0 || status.ChunksTotal != ChunkCount(size, status.ChunkSize))
            {
                throw new ClientTransferException(AcErrorCodes.InternalError, "Server returned an invalid chunk layout.");
            }

            await _db.UpsertTransferAsync(
                new ClientTransferRecord(
                    payload.TransferId,
                    TransferDirections.Download,
                    payload.WorkspaceId,
                    payload.RelPath,
                    size,
                    sha,
                    "[]",
                    TransferStatuses.Downloading),
                ct).ConfigureAwait(false);

            var buffer = new byte[status.ChunkSize];
            await using var stream = new FileStream(
                contentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            for (var index = 0; index < status.ChunksTotal; index++)
            {
                var expected = (int)Math.Min(status.ChunkSize, size - (index * (long)status.ChunkSize));
                await stream.ReadExactlyAsync(buffer.AsMemory(0, expected), ct).ConfigureAwait(false);
                await api.PutTransferChunkAsync(payload.TransferId, index, buffer.AsMemory(0, expected), token, ct)
                    .ConfigureAwait(false);
            }

            await api.CompleteTransferAsync(payload.TransferId, token, ct).ConfigureAwait(false);
            await _db.DeleteTransferAsync(payload.TransferId, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Sent transfer {TransferId} from {Path} ({Size} bytes).",
                payload.TransferId,
                payload.RelPath,
                size);
            return new FileMutationResult(payload.WorkspaceId, payload.RelPath, false);
        }
        finally
        {
            gate.Release();
            if (zipPath is not null && File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        IReadOnlyList<ClientTransferRecord> records;
        using var scope = _logger.BeginScope("Transfer resume");
        try
        {
            records = await _db.ListActiveTransfersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load pending transfers.");
            return;
        }

        foreach (var record in records)
        {
            if (!_link.TryGet(out _, out _))
            {
                return;
            }

            try
            {
                if (record.Direction == TransferDirections.Upload)
                {
                    await ReceiveAsync(
                        new FilesReceivePayload(
                            record.TransferId,
                            record.WorkspaceId,
                            Path.GetDirectoryName(record.RelPath)?.Replace('\\', '/') ?? string.Empty,
                            Path.GetFileName(record.RelPath),
                            TransferConflicts.Fail,
                            record.Sha256),
                        ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Transfer {TransferId} could not be resumed.", record.TransferId);
            }
        }
    }

    private async Task ResumeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_link.TryGet(out _, out _))
                {
                    await ResumeAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transfer resume pass failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private (ServerApiClient Api, string Token) Link()
    {
        if (!_link.TryGet(out var api, out var token))
        {
            throw new ClientTransferException(AcErrorCodes.MachineOffline, "The server link is not connected.");
        }

        return (api, token);
    }

    private string TempPath(string transferId) => Path.Combine(_tempDirectory, transferId + ".part");

    private static string ResolveConflict(string path, string conflict)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        if (string.Equals(conflict, TransferConflicts.Overwrite, StringComparison.Ordinal))
        {
            return path;
        }

        if (!string.Equals(conflict, TransferConflicts.Rename, StringComparison.Ordinal))
        {
            throw new ClientTransferException(AcErrorCodes.FileConflict, $"'{Path.GetFileName(path)}' already exists.");
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(
                directory,
                name + " (" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ClientTransferException(AcErrorCodes.FileConflict, "Could not find an available file name.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static int ChunkCount(long size, int chunkSize) =>
        size == 0 ? 0 : (int)((size + chunkSize - 1) / chunkSize);
}
