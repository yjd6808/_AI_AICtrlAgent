using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Protocol;
using AiCtrl.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiCtrl.Server.Transfers;

public sealed class TransferException : Exception
{
    public TransferException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }

    public string Code { get; }
}

public sealed class TransferStore
{
    private const string IdPrefix = "tr_";

    private readonly IDbContextFactory<AcDbContext> _factory;

    private readonly TransferOptions _options;

    private readonly TimeProvider _time;

    private readonly ILogger<TransferStore> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public TransferStore(
        IDbContextFactory<AcDbContext> factory,
        TransferOptions options,
        TimeProvider time,
        ILogger<TransferStore> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _options = options;
        _time = time;
        _logger = logger;
        Directory.CreateDirectory(_options.Dir);
    }

    public TransferOptions Options => _options;

    public async Task<TransferCreatedView> CreateUploadAsync(
        string machineId,
        string workspaceId,
        UploadCreateRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ValidateRelativeDirectory(request.RelDir);
        ValidateFileName(request.FileName);
        if (request.Size < 0 || request.Size > _options.MaxFileBytes)
        {
            throw new TransferException(
                StatusCodes.Status413PayloadTooLarge,
                AcErrorCodes.InvalidPayload,
                $"File size must be between 0 and {_options.MaxFileBytes} bytes.");
        }

        var conflict = string.IsNullOrWhiteSpace(request.Conflict) ? TransferConflicts.Fail : request.Conflict;
        if (conflict is not (TransferConflicts.Fail or TransferConflicts.Overwrite or TransferConflicts.Rename))
        {
            throw new TransferException(
                StatusCodes.Status400BadRequest,
                AcErrorCodes.InvalidPayload,
                "conflict must be fail, overwrite, or rename.");
        }

        var sha256 = NormalizeSha256(request.Sha256);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var active = await db.Transfers
            .Where(x => x.Status != TransferStatuses.Expired && x.Status != TransferStatuses.Completed)
            .Select(x => new { x.Size, x.ExpiresAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var activeBytes = active.Where(x => x.ExpiresAt > now).Sum(x => x.Size);
        if (activeBytes + request.Size > _options.MaxTotalBytes)
        {
            throw new TransferException(
                StatusCodes.Status507InsufficientStorage,
                AcErrorCodes.InvalidPayload,
                "The transfer storage quota is exhausted.");
        }

        var entity = new TransferEntity
        {
            Id = IdPrefix + Guid.NewGuid().ToString("N"),
            MachineId = machineId,
            Direction = TransferDirections.Upload,
            WorkspaceId = workspaceId,
            RelPath = Combine(request.RelDir, request.FileName),
            RelDir = request.RelDir,
            FileName = request.FileName,
            Conflict = conflict,
            AsZip = false,
            Size = request.Size,
            Sha256 = sha256,
            ChunkSize = _options.ChunkBytes,
            ChunksTotal = ChunkCount(request.Size, _options.ChunkBytes),
            ReceivedJson = "[]",
            Status = TransferStatuses.Uploading,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.Add(_options.Ttl),
        };
        db.Transfers.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Created upload transfer {TransferId} for {MachineId} ({Size} bytes).",
            entity.Id,
            machineId,
            entity.Size);
        return new TransferCreatedView(entity.Id, entity.ChunkSize, entity.ExpiresAt);
    }

    public async Task<TransferCreatedView> CreateDownloadAsync(
        string machineId,
        string workspaceId,
        DownloadCreateRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (string.IsNullOrWhiteSpace(request.RelPath))
        {
            throw new TransferException(
                StatusCodes.Status400BadRequest,
                AcErrorCodes.InvalidPayload,
                "relPath is required.");
        }

        ValidateRelativePath(request.RelPath);
        var now = _time.GetUtcNow();
        var entity = new TransferEntity
        {
            Id = IdPrefix + Guid.NewGuid().ToString("N"),
            MachineId = machineId,
            Direction = TransferDirections.Download,
            WorkspaceId = workspaceId,
            RelPath = request.RelPath,
            FileName = DownloadFileName(request.RelPath, request.AsZip),
            AsZip = request.AsZip,
            Size = 0,
            ChunkSize = _options.ChunkBytes,
            ChunksTotal = 0,
            ReceivedJson = "[]",
            Status = TransferStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.Add(_options.Ttl),
        };
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Transfers.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Created download transfer {TransferId} for {MachineId}.", entity.Id, machineId);
        return new TransferCreatedView(entity.Id, entity.ChunkSize, entity.ExpiresAt);
    }

    public async Task<TransferStatusView> BeginDownloadAsync(
        string transferId,
        long size,
        string? sha256,
        CancellationToken ct)
    {
        if (size < 0 || size > _options.MaxFileBytes)
        {
            throw new TransferException(
                StatusCodes.Status413PayloadTooLarge,
                AcErrorCodes.InvalidPayload,
                $"Transfer size must be between 0 and {_options.MaxFileBytes} bytes.");
        }

        var gate = Gate(transferId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await RequireAsync(db, transferId, ct).ConfigureAwait(false);
            if (entity.Direction != TransferDirections.Download)
            {
                throw new TransferException(
                    StatusCodes.Status409Conflict,
                    AcErrorCodes.InvalidPayload,
                    "The transfer is not a download.");
            }

            var normalized = NormalizeSha256(sha256);
            var layoutChanged = entity.Size != size
                || !string.Equals(entity.Sha256, normalized, StringComparison.OrdinalIgnoreCase);
            entity.Size = size;
            entity.Sha256 = normalized;
            entity.ChunksTotal = ChunkCount(size, entity.ChunkSize);
            if (layoutChanged)
            {
                entity.ReceivedJson = "[]";
            }

            entity.Status = TransferStatuses.Downloading;
            entity.UpdatedAt = _time.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToView(entity);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TransferEntity> RequireAsync(string transferId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await RequireAsync(db, transferId, ct).ConfigureAwait(false);
    }

    public async Task<TransferStatusView> GetStatusAsync(string transferId, CancellationToken ct) =>
        ToView(await RequireAsync(transferId, ct).ConfigureAwait(false));

    public async Task<TransferStatusView> WriteChunkAsync(
        string transferId,
        int index,
        Stream body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var gate = Gate(transferId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await RequireAsync(db, transferId, ct).ConfigureAwait(false);
            if (entity.Status is TransferStatuses.Completed or TransferStatuses.Expired or TransferStatuses.Failed)
            {
                throw new TransferException(
                    StatusCodes.Status409Conflict,
                    AcErrorCodes.InvalidPayload,
                    $"The transfer is {entity.Status}.");
            }

            if (entity.ChunksTotal == 0 || index < 0 || index >= entity.ChunksTotal)
            {
                throw new TransferException(
                    StatusCodes.Status416RangeNotSatisfiable,
                    AcErrorCodes.InvalidPayload,
                    "Chunk index is outside the transfer.");
            }

            var offset = (long)index * entity.ChunkSize;
            var expected = Math.Min(entity.ChunkSize, entity.Size - offset);
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, ct).ConfigureAwait(false);
            if (buffer.Length != expected)
            {
                throw new TransferException(
                    StatusCodes.Status400BadRequest,
                    AcErrorCodes.InvalidPayload,
                    $"Chunk {index} must be {expected} bytes but was {buffer.Length}.");
            }

            var path = DataPath(entity.Id);
            Directory.CreateDirectory(_options.Dir);
            await using (var file = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous))
            {
                file.Seek(offset, SeekOrigin.Begin);
                buffer.Position = 0;
                await buffer.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            var received = ReadReceived(entity.ReceivedJson);
            if (!received.Contains(index))
            {
                received.Add(index);
                received.Sort();
            }

            entity.ReceivedJson = JsonSerializer.Serialize(received);
            entity.Status = entity.Direction == TransferDirections.Upload
                ? TransferStatuses.Uploading
                : TransferStatuses.Downloading;
            entity.UpdatedAt = _time.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToView(entity);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TransferStatusView> CompleteAsync(string transferId, CancellationToken ct)
    {
        var gate = Gate(transferId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await RequireAsync(db, transferId, ct).ConfigureAwait(false);
            if (entity.Status == TransferStatuses.Ready)
            {
                return ToView(entity);
            }

            var received = ReadReceived(entity.ReceivedJson);
            if (received.Count != entity.ChunksTotal)
            {
                throw new TransferException(
                    StatusCodes.Status409Conflict,
                    AcErrorCodes.InvalidPayload,
                    $"Only {received.Count} of {entity.ChunksTotal} chunks were received.");
            }

            var path = DataPath(entity.Id);
            if (entity.Size == 0 && !File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, [], ct).ConfigureAwait(false);
            }

            if (!File.Exists(path))
            {
                throw new TransferException(
                    StatusCodes.Status409Conflict,
                    AcErrorCodes.InvalidPayload,
                    "Transfer content is missing.");
            }

            var info = new FileInfo(path);
            if (info.Length != entity.Size)
            {
                throw new TransferException(
                    StatusCodes.Status409Conflict,
                    AcErrorCodes.InvalidPayload,
                    $"Transfer size is {info.Length} but expected {entity.Size}.");
            }

            if (!string.IsNullOrWhiteSpace(entity.Sha256))
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(actual, entity.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    entity.Status = TransferStatuses.Failed;
                    entity.Error = "sha256 mismatch";
                    entity.UpdatedAt = _time.GetUtcNow();
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    throw new TransferException(
                        StatusCodes.Status422UnprocessableEntity,
                        AcErrorCodes.InvalidPayload,
                        $"sha256 mismatch: expected {entity.Sha256}, got {actual}.");
                }
            }

            entity.Status = TransferStatuses.Ready;
            entity.UpdatedAt = _time.GetUtcNow();
            entity.Error = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Transfer {TransferId} is ready ({Size} bytes).", entity.Id, entity.Size);
            return ToView(entity);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReleaseAsync(string transferId, CancellationToken ct)
    {
        var gate = Gate(transferId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await RequireAsync(db, transferId, ct).ConfigureAwait(false);
            entity.Status = TransferStatuses.Completed;
            entity.UpdatedAt = _time.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var path = DataPath(entity.Id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            _gates.TryRemove(transferId, out _);
        }
        finally
        {
            gate.Release();
        }
    }

    public string DataPath(string transferId)
    {
        if (transferId.Length != IdPrefix.Length + 32
            || !transferId.StartsWith(IdPrefix, StringComparison.Ordinal)
            || !transferId.AsSpan(IdPrefix.Length).ToString().All(Uri.IsHexDigit))
        {
            throw new TransferException(
                StatusCodes.Status400BadRequest,
                AcErrorCodes.InvalidPayload,
                "Transfer id is invalid.");
        }

        return Path.Combine(_options.Dir, transferId + ".bin");
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var candidates = await db.Transfers
            .Where(x => x.Status != TransferStatuses.Expired
                && x.Status != TransferStatuses.Completed)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var expired = candidates.Where(x => x.ExpiresAt <= now).ToList();
        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var entity in expired)
        {
            entity.Status = TransferStatuses.Expired;
            entity.UpdatedAt = now;
            entity.Error = "transfer expired";
            TryDeleteDataFile(entity.Id);
            _gates.TryRemove(entity.Id, out _);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Expired {Count} transfers.", expired.Count);
        return expired.Count;
    }

    private async Task<TransferEntity> RequireAsync(
        AcDbContext db,
        string transferId,
        CancellationToken ct)
    {
        var entity = await db.Transfers.SingleOrDefaultAsync(x => x.Id == transferId, ct).ConfigureAwait(false)
            ?? throw new TransferException(
                StatusCodes.Status404NotFound,
                AcErrorCodes.InvalidPayload,
                $"Transfer '{transferId}' was not found.");

        if (entity.ExpiresAt <= _time.GetUtcNow() && entity.Status != TransferStatuses.Completed)
        {
            entity.Status = TransferStatuses.Expired;
            entity.UpdatedAt = _time.GetUtcNow();
            entity.Error = "transfer expired";
            TryDeleteDataFile(entity.Id);
            _gates.TryRemove(entity.Id, out _);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw new TransferException(
                StatusCodes.Status410Gone,
                AcErrorCodes.TransferExpired,
                $"Transfer '{transferId}' has expired.");
        }

        return entity;
    }

    private void TryDeleteDataFile(string transferId)
    {
        try
        {
            var path = DataPath(transferId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete expired transfer {TransferId}.", transferId);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to delete expired transfer {TransferId}.", transferId);
        }
    }

    private static TransferStatusView ToView(TransferEntity entity) =>
        new(
            entity.Id,
            entity.Direction,
            entity.MachineId,
            entity.WorkspaceId,
            entity.RelPath,
            entity.Size,
            entity.Sha256,
            entity.AsZip,
            entity.Conflict,
            entity.ChunkSize,
            entity.ChunksTotal,
            ReadReceived(entity.ReceivedJson),
            entity.Status,
            entity.CreatedAt,
            entity.ExpiresAt,
            entity.Error);

    private SemaphoreSlim Gate(string transferId) =>
        _gates.GetOrAdd(transferId, _ => new SemaphoreSlim(1, 1));

    private static List<int> ReadReceived(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<int[]>(json) ?? [];
            Array.Sort(values);
            return [.. values];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int ChunkCount(long size, int chunkSize) =>
        size == 0 ? 0 : (int)((size + chunkSize - 1) / chunkSize);

    private static string DownloadFileName(string relPath, bool asZip)
    {
        var name = Path.GetFileName(relPath.TrimEnd('/', '\\'));
        if (!asZip)
        {
            return name;
        }

        return (string.IsNullOrWhiteSpace(name) ? "download" : name)
            + (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? string.Empty : ".zip");
    }

    private static string Combine(string relDir, string fileName) =>
        string.IsNullOrWhiteSpace(relDir) ? fileName : relDir.TrimEnd('/') + "/" + fileName;

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new TransferException(
                StatusCodes.Status400BadRequest,
                AcErrorCodes.InvalidPayload,
                "sha256 must be 64 hexadecimal characters.");
        }

        return normalized;
    }

    private static void ValidateFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
        {
            throw new TransferException(
                StatusCodes.Status400BadRequest,
                AcErrorCodes.PathInvalid,
                "fileName is invalid.");
        }
    }

    private static void ValidateRelativeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        ValidateRelativePath(value);
    }

    private static void ValidateRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Split('/').Any(part => part is "" or "." or "..")
            || normalized.Any(char.IsControl))
        {
            throw new TransferException(
                StatusCodes.Status400BadRequest,
                AcErrorCodes.PathInvalid,
                "Path must be a safe workspace-relative path.");
        }
    }
}
