using AiCtrl.Core.Api;

namespace AiCtrl.Core.Workspace;

public sealed class WorkspaceException : Exception
{
    public WorkspaceException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public static WorkspaceException NotFound(string workspaceId) =>
        new(AcErrorCodes.WorkspaceNotAllowed, $"Workspace '{workspaceId}' is not registered.");
}
