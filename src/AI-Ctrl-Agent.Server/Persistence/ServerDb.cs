using System.Text.Json;
using AiCtrl.Core.Api;
using AiCtrl.Core.Commands;
using AiCtrl.Core.Domain;
using AiCtrl.Core.Events;
using AiCtrl.Core.Protocol;
using AiCtrl.Core.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AiCtrl.Server.Persistence;

public sealed record EnrollmentResult(bool Success, string? ErrorCode, MachineEntity? Machine);

public sealed record HelloRecord(bool Found, bool Revoked, bool SnapshotRequired, long LastAppliedEventSeq);

public sealed record ApplyEventsResult(long LastAppliedEventSeq, IReadOnlyList<VpnStatusView> VpnStates);

public sealed class ServerDb
{
    private readonly IDbContextFactory<AcDbContext> _factory;

    private readonly TimeProvider _time;

    public ServerDb(IDbContextFactory<AcDbContext> factory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(time);
        _factory = factory;
        _time = time;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS commands (
                id TEXT NOT NULL PRIMARY KEY,
                machine_id TEXT NOT NULL,
                type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                status INTEGER NOT NULL,
                issued_by TEXT NOT NULL,
                issued_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                result_json TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                updated_at TEXT NOT NULL,
                trace_id TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_commands_machine_issued ON commands (machine_id, issued_at);
            CREATE INDEX IF NOT EXISTS ix_commands_status ON commands (status);
            CREATE TABLE IF NOT EXISTS command_steps (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                command_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                step TEXT NOT NULL,
                message TEXT NULL,
                at TEXT NOT NULL,
                UNIQUE (command_id, seq)
            );
            CREATE TABLE IF NOT EXISTS idempotency_keys (
                user_id TEXT NOT NULL,
                key TEXT NOT NULL,
                command_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (user_id, key)
            );
            CREATE TABLE IF NOT EXISTS query_cache (
                machine_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                as_of TEXT NOT NULL,
                PRIMARY KEY (machine_id, cache_key)
            );
            CREATE TABLE IF NOT EXISTS transfers (
                id TEXT NOT NULL PRIMARY KEY,
                machine_id TEXT NOT NULL,
                direction TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                rel_path TEXT NOT NULL,
                rel_dir TEXT NULL,
                file_name TEXT NOT NULL,
                conflict TEXT NULL,
                as_zip INTEGER NOT NULL,
                size INTEGER NOT NULL,
                sha256 TEXT NULL,
                chunk_size INTEGER NOT NULL,
                chunks_total INTEGER NOT NULL,
                received_json TEXT NOT NULL,
                status TEXT NOT NULL,
                error TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_transfers_machine_created ON transfers (machine_id, created_at);
            CREATE INDEX IF NOT EXISTS ix_transfers_status ON transfers (status);
            CREATE TABLE IF NOT EXISTS machine_vpn_state (
                machine_id TEXT NOT NULL PRIMARY KEY,
                payload_json TEXT NOT NULL,
                as_of TEXT NOT NULL
            );
            """,
            ct).ConfigureAwait(false);
    }

    public async Task CreateEnrollmentCodeAsync(string codeHash, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.EnrollmentCodes.Add(new EnrollmentCodeEntity
        {
            CodeHash = codeHash,
            ExpiresAt = expiresAt,
            CreatedAt = _time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<EnrollmentResult> EnrollAsync(
        string codeHash,
        string machineId,
        string hostname,
        string os,
        string publicKey,
        string clientVersion,
        string actor,
        string? ip,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var code = await db.EnrollmentCodes.SingleOrDefaultAsync(x => x.CodeHash == codeHash, ct).ConfigureAwait(false);
        if (code is null || code.UsedAt is not null)
        {
            return new EnrollmentResult(false, "invalid_code", null);
        }

        if (code.ExpiresAt <= now)
        {
            return new EnrollmentResult(false, "enrollment_expired", null);
        }

        code.UsedAt = now;
        var machine = new MachineEntity
        {
            Id = machineId,
            Hostname = hostname,
            Os = os,
            PublicKey = publicKey,
            ClientVersion = clientVersion,
            EnrolledAt = now,
            LastAppliedEventSeq = 0,
        };
        db.Machines.Add(machine);
        db.AuditLog.Add(new AuditLogEntity
        {
            At = now,
            Actor = actor,
            Ip = ip,
            Action = "machine.enroll",
            Target = machineId,
            Result = "success",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new EnrollmentResult(true, null, machine);
    }

    public async Task<UserEntity> GetOrCreateUserAsync(string email, string? name, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (user is null)
        {
            user = new UserEntity
            {
                Id = "u_" + Guid.NewGuid().ToString("N"),
                Email = email,
                Name = name,
                CreatedAt = now,
                LastLoginAt = now,
            };
            db.Users.Add(user);
        }
        else
        {
            user.Name = name ?? user.Name;
            user.LastLoginAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    public async Task<MachineEntity?> GetMachineAsync(string machineId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Machines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == machineId, ct).ConfigureAwait(false);
    }

    public async Task<HelloRecord> RecordHelloAsync(
        string machineId,
        string bootId,
        long lastAckedEventSeq,
        string? ip,
        string? country,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var machine = await db.Machines.SingleOrDefaultAsync(x => x.Id == machineId, ct).ConfigureAwait(false);
        if (machine is null)
        {
            return new HelloRecord(false, false, false, 0);
        }

        if (machine.RevokedAt is not null)
        {
            return new HelloRecord(true, true, false, machine.LastAppliedEventSeq);
        }

        machine.LastSeenAt = _time.GetUtcNow();
        machine.BootId = bootId;
        machine.ObservedIp = ip;
        machine.ObservedCountry = country;
        var snapshotRequired = machine.LastAppliedEventSeq > lastAckedEventSeq;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new HelloRecord(true, false, snapshotRequired, machine.LastAppliedEventSeq);
    }

    public async Task<ApplyEventsResult> ApplyEventsAsync(
        string machineId,
        IReadOnlyList<AgentEventEnvelope> events,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var machine = await db.Machines.SingleOrDefaultAsync(x => x.Id == machineId, ct).ConfigureAwait(false);
        if (machine is null)
        {
            return new ApplyEventsResult(-1, Array.Empty<VpnStatusView>());
        }

        var appliedSeq = machine.LastAppliedEventSeq;
        var maxSeq = events.Count == 0 ? appliedSeq : events.Max(x => x.Seq);
        if (maxSeq > appliedSeq)
        {
            appliedSeq = maxSeq;
        }

        VpnStatusView? vpnState = null;
        foreach (var envelope in events
                     .Where(x => x.Seq > machine.LastAppliedEventSeq && x.Type == AgentEventTypes.VpnState)
                     .OrderBy(x => x.Seq))
        {
            VpnStatusView? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(envelope.Payload, AcJsonContext.Default.VpnStatusView);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is null || parsed.AsOf == default)
            {
                continue;
            }

            var entity = await db.MachineVpnStates
                .SingleOrDefaultAsync(x => x.MachineId == machineId, ct)
                .ConfigureAwait(false);
            if (entity is null)
            {
                entity = new MachineVpnStateEntity { MachineId = machineId };
                db.MachineVpnStates.Add(entity);
            }

            entity.PayloadJson = JsonSerializer.Serialize(parsed, AcJsonContext.Default.VpnStatusView);
            entity.AsOf = parsed.AsOf;
            vpnState = parsed;
        }

        machine.LastAppliedEventSeq = appliedSeq;
        machine.LastSeenAt = _time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ApplyEventsResult(
            machine.LastAppliedEventSeq,
            vpnState is null ? Array.Empty<VpnStatusView>() : new[] { vpnState });
    }

    public async Task<VpnStatusView?> GetVpnStateAsync(string machineId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.MachineVpnStates
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.MachineId == machineId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ParseVpnState(entity.PayloadJson);
    }

    public async Task<Dictionary<string, VpnStatusView>> GetVpnStatesAsync(
        IReadOnlyCollection<string> machineIds,
        CancellationToken ct)
    {
        if (machineIds.Count == 0)
        {
            return new Dictionary<string, VpnStatusView>(StringComparer.Ordinal);
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var states = await db.MachineVpnStates
            .AsNoTracking()
            .Where(x => machineIds.Contains(x.MachineId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var result = new Dictionary<string, VpnStatusView>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var parsed = ParseVpnState(state.PayloadJson);
            if (parsed is not null)
            {
                result[state.MachineId] = parsed;
            }
        }

        return result;
    }

    public async Task RevokeMachineAsync(string machineId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var machine = await db.Machines.SingleOrDefaultAsync(x => x.Id == machineId, ct).ConfigureAwait(false);
        if (machine is null)
        {
            return;
        }

        machine.RevokedAt = _time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAuditAsync(AuditLogEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AuditLog.Add(new AuditLogEntity
        {
            At = _time.GetUtcNow(),
            Actor = entry.Actor,
            Ip = entry.Ip,
            Country = entry.Country,
            Action = entry.Action,
            Target = entry.Target,
            CommandId = entry.CommandId,
            Result = entry.Result,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CreateCommandAsync(CommandEnvelope envelope, string traceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Commands.Add(new CommandEntity
        {
            Id = envelope.CommandId,
            MachineId = envelope.Machine.Value,
            Type = envelope.Type,
            PayloadJson = envelope.Payload.GetRawText(),
            Status = CommandStatus.Dispatched,
            IssuedBy = envelope.IssuedBy,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Attempt = envelope.Attempt,
            UpdatedAt = _time.GetUtcNow(),
            TraceId = traceId,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkCommandAcknowledgedAsync(string commandId, string machineId, DateTimeOffset at, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var command = await db.Commands
            .SingleOrDefaultAsync(x => x.Id == commandId && x.MachineId == machineId, ct)
            .ConfigureAwait(false);
        if (command is null)
        {
            return;
        }

        if (!CommandStateMachine.IsTerminal(command.Status)
            && CommandStateMachine.CanTransition(command.Status, CommandStatus.Acknowledged))
        {
            command.Status = CommandStatus.Acknowledged;
            command.UpdatedAt = at;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task AddCommandStepAsync(
        string commandId,
        string machineId,
        string step,
        string? message,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var command = await db.Commands
            .SingleOrDefaultAsync(x => x.Id == commandId && x.MachineId == machineId, ct)
            .ConfigureAwait(false);
        if (command is null)
        {
            return;
        }

        if (!CommandStateMachine.IsTerminal(command.Status)
            && CommandStateMachine.CanTransition(command.Status, CommandStatus.Running))
        {
            command.Status = CommandStatus.Running;
            command.UpdatedAt = at;
        }

        var seq = await db.CommandSteps
            .Where(x => x.CommandId == commandId)
            .Select(x => (int?)x.Seq)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0;

        db.CommandSteps.Add(new CommandStepEntity
        {
            CommandId = commandId,
            Seq = seq + 1,
            Step = step,
            Message = message,
            At = at,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteCommandAsync(ResponsePacket response, string machineId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var command = await db.Commands
            .SingleOrDefaultAsync(x => x.Id == response.RequestId && x.MachineId == machineId, ct)
            .ConfigureAwait(false);
        if (command is null || CommandStateMachine.IsTerminal(command.Status))
        {
            return;
        }

        command.Status = response.Status switch
        {
            ResponseStatus.Succeeded => CommandStatus.Succeeded,
            ResponseStatus.Failed => CommandStatus.Failed,
            ResponseStatus.Cancelled => CommandStatus.Cancelled,
            ResponseStatus.Expired => CommandStatus.Expired,
            _ => CommandStatus.Unknown,
        };
        command.ResultJson = response.Data?.GetRawText();
        command.ErrorCode = response.ErrorCode;
        command.ErrorMessage = response.ErrorMessage;
        command.UpdatedAt = response.AsOf;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkCommandFailedAsync(
        string commandId,
        string machineId,
        string errorCode,
        string errorMessage,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var command = await db.Commands
            .SingleOrDefaultAsync(x => x.Id == commandId && x.MachineId == machineId, ct)
            .ConfigureAwait(false);
        if (command is null || CommandStateMachine.IsTerminal(command.Status))
        {
            return;
        }

        command.Status = CommandStatus.Failed;
        command.ErrorCode = errorCode;
        command.ErrorMessage = errorMessage;
        command.UpdatedAt = _time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<CommandDetailView?> GetCommandAsync(string commandId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var command = await db.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == commandId, ct).ConfigureAwait(false);
        if (command is null)
        {
            return null;
        }

        var steps = await db.CommandSteps
            .AsNoTracking()
            .Where(x => x.CommandId == commandId)
            .OrderBy(x => x.Seq)
            .Select(x => new CommandStepView(x.Seq, x.Step, x.Message, x.At))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return ToDetail(command, steps);
    }

    public async Task<List<CommandDetailView>> ListCommandsAsync(
        string? machineId,
        CommandStatus? status,
        int limit,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Commands.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            query = query.Where(x => x.MachineId == machineId);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var commands = await query
            .OrderByDescending(x => x.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var ids = commands.Select(x => x.Id).ToArray();
        var steps = await db.CommandSteps
            .AsNoTracking()
            .Where(x => ids.Contains(x.CommandId))
            .OrderBy(x => x.Seq)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byCommand = steps
            .GroupBy(x => x.CommandId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CommandStepView>)group
                    .Select(x => new CommandStepView(x.Seq, x.Step, x.Message, x.At))
                    .ToList(),
                StringComparer.Ordinal);

        return commands
            .Select(command => ToDetail(
                command,
                byCommand.GetValueOrDefault(command.Id) ?? Array.Empty<CommandStepView>()))
            .ToList();
    }

    public async Task<string?> GetIdempotentCommandIdAsync(string userId, string key, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.IdempotencyKeys
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.Key == key)
            .Select(x => x.CommandId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task SaveIdempotencyKeyAsync(
        string userId,
        string key,
        string commandId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.IdempotencyKeys.Add(new IdempotencyKeyEntity
        {
            UserId = userId,
            Key = key,
            CommandId = commandId,
            CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task SetQueryCacheAsync(
        string machineId,
        string cacheKey,
        string payloadJson,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.QueryCache
            .SingleOrDefaultAsync(x => x.MachineId == machineId && x.CacheKey == cacheKey, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            db.QueryCache.Add(new QueryCacheEntity
            {
                MachineId = machineId,
                CacheKey = cacheKey,
                PayloadJson = payloadJson,
                AsOf = asOf,
            });
        }
        else
        {
            existing.PayloadJson = payloadJson;
            existing.AsOf = asOf;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(string PayloadJson, DateTimeOffset AsOf)?> GetQueryCacheAsync(
        string machineId,
        string cacheKey,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entry = await db.QueryCache
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.MachineId == machineId && x.CacheKey == cacheKey, ct)
            .ConfigureAwait(false);
        return entry is null ? null : (entry.PayloadJson, entry.AsOf);
    }

    public async Task<List<MachineEntity>> GetMachinesAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Machines
            .AsNoTracking()
            .Where(x => x.RevokedAt == null)
            .OrderBy(x => x.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<AuditLogView>> ListAuditAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.AuditLog.AsNoTracking();
        if (from is not null)
        {
            query = query.Where(x => x.At >= from);
        }

        if (to is not null)
        {
            query = query.Where(x => x.At <= to);
        }

        return await query
            .OrderByDescending(x => x.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(x => new AuditLogView(
                x.Id,
                x.At,
                x.Actor,
                x.Ip,
                x.Country,
                x.Action,
                x.Target,
                x.CommandId,
                x.Result))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private static VpnStatusView? ParseVpnState(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, AcJsonContext.Default.VpnStatusView);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CommandDetailView ToDetail(CommandEntity command, IReadOnlyList<CommandStepView> steps) =>
        new(
            command.Id,
            command.Type,
            command.Status,
            ParseResult(command.ResultJson),
            command.ErrorCode,
            command.ErrorMessage,
            command.IssuedBy,
            command.IssuedAt,
            command.UpdatedAt,
            steps);

    private static JsonElement? ParseResult(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed record AuditLogEntry(
    string Actor,
    string? Ip,
    string? Country,
    string Action,
    string? Target,
    string? CommandId,
    string Result);
