namespace AiCtrl.Client.Workspace;

public sealed record WorkspaceDefinition(
    string Name,
    string Root,
    bool AllowUpload,
    bool AllowDownload,
    bool AllowMkdir,
    bool AllowDelete,
    IReadOnlyList<string> DenyPatterns);
