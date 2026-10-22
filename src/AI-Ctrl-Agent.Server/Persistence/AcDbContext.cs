using Microsoft.EntityFrameworkCore;

namespace AiCtrl.Server.Persistence;

public sealed class AcDbContext : DbContext
{
    public AcDbContext(DbContextOptions<AcDbContext> options)
        : base(options)
    {
    }

    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<MachineEntity> Machines => Set<MachineEntity>();

    public DbSet<EnrollmentCodeEntity> EnrollmentCodes => Set<EnrollmentCodeEntity>();

    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();

    public DbSet<CommandEntity> Commands => Set<CommandEntity>();

    public DbSet<CommandStepEntity> CommandSteps => Set<CommandStepEntity>();

    public DbSet<IdempotencyKeyEntity> IdempotencyKeys => Set<IdempotencyKeyEntity>();

    public DbSet<QueryCacheEntity> QueryCache => Set<QueryCacheEntity>();

    public DbSet<TransferEntity> Transfers => Set<TransferEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Email).HasColumnName("email").IsRequired();
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            entity.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<MachineEntity>(entity =>
        {
            entity.ToTable("machines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Hostname).HasColumnName("hostname").IsRequired();
            entity.Property(x => x.Os).HasColumnName("os").IsRequired();
            entity.Property(x => x.PublicKey).HasColumnName("public_key").IsRequired();
            entity.Property(x => x.ClientVersion).HasColumnName("client_version").IsRequired();
            entity.Property(x => x.EnrolledAt).HasColumnName("enrolled_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(x => x.LastAppliedEventSeq).HasColumnName("last_applied_event_seq");
            entity.Property(x => x.ObservedIp).HasColumnName("observed_ip");
            entity.Property(x => x.ObservedCountry).HasColumnName("observed_country");
            entity.Property(x => x.BootId).HasColumnName("boot_id");
        });

        modelBuilder.Entity<EnrollmentCodeEntity>(entity =>
        {
            entity.ToTable("enrollment_codes");
            entity.HasKey(x => x.CodeHash);
            entity.Property(x => x.CodeHash).HasColumnName("code_hash");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.UsedAt).HasColumnName("used_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<AuditLogEntity>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.At).HasColumnName("at");
            entity.Property(x => x.Actor).HasColumnName("actor");
            entity.Property(x => x.Ip).HasColumnName("ip");
            entity.Property(x => x.Country).HasColumnName("country");
            entity.Property(x => x.Action).HasColumnName("action").IsRequired();
            entity.Property(x => x.Target).HasColumnName("target");
            entity.Property(x => x.CommandId).HasColumnName("command_id");
            entity.Property(x => x.Result).HasColumnName("result").IsRequired();
        });

        modelBuilder.Entity<CommandEntity>(entity =>
        {
            entity.ToTable("commands");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.MachineId).HasColumnName("machine_id").IsRequired();
            entity.Property(x => x.Type).HasColumnName("type").IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").IsRequired();
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.IssuedBy).HasColumnName("issued_by").IsRequired();
            entity.Property(x => x.IssuedAt).HasColumnName("issued_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.ResultJson).HasColumnName("result_json");
            entity.Property(x => x.ErrorCode).HasColumnName("error_code");
            entity.Property(x => x.ErrorMessage).HasColumnName("error_message");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.TraceId).HasColumnName("trace_id").IsRequired();
            entity.HasIndex(x => new { x.MachineId, x.IssuedAt });
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<CommandStepEntity>(entity =>
        {
            entity.ToTable("command_steps");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.CommandId).HasColumnName("command_id").IsRequired();
            entity.Property(x => x.Seq).HasColumnName("seq");
            entity.Property(x => x.Step).HasColumnName("step").IsRequired();
            entity.Property(x => x.Message).HasColumnName("message");
            entity.Property(x => x.At).HasColumnName("at");
            entity.HasIndex(x => new { x.CommandId, x.Seq }).IsUnique();
        });

        modelBuilder.Entity<IdempotencyKeyEntity>(entity =>
        {
            entity.ToTable("idempotency_keys");
            entity.HasKey(x => new { x.UserId, x.Key });
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Key).HasColumnName("key");
            entity.Property(x => x.CommandId).HasColumnName("command_id").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<QueryCacheEntity>(entity =>
        {
            entity.ToTable("query_cache");
            entity.HasKey(x => new { x.MachineId, x.CacheKey });
            entity.Property(x => x.MachineId).HasColumnName("machine_id");
            entity.Property(x => x.CacheKey).HasColumnName("cache_key");
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").IsRequired();
            entity.Property(x => x.AsOf).HasColumnName("as_of");
        });

        modelBuilder.Entity<TransferEntity>(entity =>
        {
            entity.ToTable("transfers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.MachineId).HasColumnName("machine_id").IsRequired();
            entity.Property(x => x.Direction).HasColumnName("direction").IsRequired();
            entity.Property(x => x.WorkspaceId).HasColumnName("workspace_id").IsRequired();
            entity.Property(x => x.RelPath).HasColumnName("rel_path").IsRequired();
            entity.Property(x => x.RelDir).HasColumnName("rel_dir");
            entity.Property(x => x.FileName).HasColumnName("file_name").IsRequired();
            entity.Property(x => x.Conflict).HasColumnName("conflict");
            entity.Property(x => x.AsZip).HasColumnName("as_zip");
            entity.Property(x => x.Size).HasColumnName("size");
            entity.Property(x => x.Sha256).HasColumnName("sha256");
            entity.Property(x => x.ChunkSize).HasColumnName("chunk_size");
            entity.Property(x => x.ChunksTotal).HasColumnName("chunks_total");
            entity.Property(x => x.ReceivedJson).HasColumnName("received_json").IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").IsRequired();
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.HasIndex(x => new { x.MachineId, x.CreatedAt });
            entity.HasIndex(x => x.Status);
        });
    }
}

public sealed class UserEntity
{
    public string Id { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class MachineEntity
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string Hostname { get; set; } = string.Empty;

    public string Os { get; set; } = string.Empty;

    public string PublicKey { get; set; } = string.Empty;

    public string ClientVersion { get; set; } = string.Empty;

    public DateTimeOffset EnrolledAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public long LastAppliedEventSeq { get; set; }

    public string? ObservedIp { get; set; }

    public string? ObservedCountry { get; set; }

    public string? BootId { get; set; }
}

public sealed class TransferEntity
{
    public string Id { get; set; } = string.Empty;

    public string MachineId { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty;

    public string WorkspaceId { get; set; } = string.Empty;

    public string RelPath { get; set; } = string.Empty;

    public string? RelDir { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string? Conflict { get; set; }

    public bool AsZip { get; set; }

    public long Size { get; set; }

    public string? Sha256 { get; set; }

    public int ChunkSize { get; set; }

    public int ChunksTotal { get; set; }

    public string ReceivedJson { get; set; } = "[]";

    public string Status { get; set; } = string.Empty;

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class EnrollmentCodeEntity
{
    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AuditLogEntity
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string? Ip { get; set; }

    public string? Country { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? Target { get; set; }

    public string? CommandId { get; set; }

    public string Result { get; set; } = string.Empty;
}
