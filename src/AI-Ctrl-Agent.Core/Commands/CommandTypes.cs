namespace AiCtrl.Core.Commands;

public static class CommandTypes
{
    public const string InstanceRefresh = "instance.refresh";

    public const string SessionAbort = "session.abort";

    public const string SessionPrompt = "session.prompt";

    public const string SessionAbortAndPrompt = "session.abort_and_prompt";

    public const string SessionCreate = "session.create";

    public const string SessionPermissionReply = "session.permission.reply";

    public const string MachineAbortAll = "machine.abort_all";

    public const string OpenCodeBackendRestart = "opencode.backend.restart";

    public const string VpnConnect = "vpn.connect";

    public const string VpnDisconnect = "vpn.disconnect";

    public const string VpnPolicySet = "vpn.policy.set";

    public const string FilesReceive = "files.receive";

    public const string FilesSend = "files.send";

    public const string FilesMkdir = "files.mkdir";

    public const string FilesDelete = "files.delete";

    public const string FilesRename = "files.rename";

    public const string WorkspaceRegister = "workspace.register";

    public const string PowerKeepAwake = "power.keep_awake";
}
