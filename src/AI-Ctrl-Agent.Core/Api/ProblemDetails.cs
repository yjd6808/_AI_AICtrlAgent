namespace AiCtrl.Core.Api;

public sealed record AcProblemDetails(
    string Type,
    string Title,
    int Status,
    string? Detail,
    string? Instance,
    string Code);

public static class AcErrorCodes
{
    public const string GeoBlocked = "geo_blocked";

    public const string ReauthRequired = "reauth_required";

    public const string MachineOffline = "machine_offline";

    public const string AgentNoAck = "agent_no_ack";

    public const string ServerRestarting = "server_restarting";

    public const string VpnBusy = "vpn_busy";

    public const string CapabilityMissing = "capability_missing";

    public const string Busy = "busy";

    public const string ForeignTurn = "foreign_turn";

    public const string AbortTimeout = "abort_timeout";

    public const string NotSettled = "not_settled";

    public const string PromptUnconfirmed = "prompt_unconfirmed";

    public const string VpnPrivilege = "vpn_privilege";

    public const string VpnSigninRequired = "vpn_signin_required";

    public const string ProtonGuiRunning = "proton_gui_running";

    public const string SessionsBusy = "sessions_busy";

    public const string PathDenied = "path_denied";

    public const string PathInvalid = "path_invalid";

    public const string FileConflict = "file_conflict";

    public const string HashMismatch = "hash_mismatch";

    public const string TransferExpired = "transfer_expired";

    public const string QueryTimeout = "query_timeout";

    public const string QueryFailed = "query_failed";

    public const string WorkspaceNotAllowed = "workspace_not_allowed";

    public const string SessionNotFound = "session_not_found";

    public const string InstanceNotFound = "instance_not_found";

    public const string InvalidPayload = "invalid_payload";

    public const string UnsupportedCommand = "unsupported_command";

    public const string InternalError = "internal_error";

    public const string DuplicateCommand = "duplicate_command";

    public const string CommandNotFound = "command_not_found";

    public const string Unauthorized = "unauthorized";

    public const string InvalidHost = "invalid_host";

    public const string InvalidCode = "invalid_code";

    public const string EnrollmentExpired = "enrollment_expired";

    public const string MachineRevoked = "machine_revoked";

    public const string InvalidSignature = "invalid_signature";

    public const string ChallengeExpired = "challenge_expired";

    public const string MachineNotFound = "machine_not_found";

    public const string UnsupportedProtocol = "unsupported_protocol";

    public const string MachineReplaced = "machine_replaced";

    public const string AuthUnavailable = "auth_unavailable";

    public const string EmailNotAllowed = "email_not_allowed";
}
