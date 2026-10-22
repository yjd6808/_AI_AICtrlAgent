using AiCtrl.Client.Storage;
using AiCtrl.Client.Transfers;
using AiCtrl.Client.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCtrl.Tests.Client;

internal static class TestTransferSystem
{
    public static TransferSystem Create(
        ClientDb db,
        WorkspaceRegistry registry,
        TransferLinkState? link = null,
        TimeProvider? time = null) =>
        new(
            db,
            registry,
            link ?? new TransferLinkState(),
            time ?? TimeProvider.System,
            NullLogger<TransferSystem>.Instance,
            Path.Combine(Path.GetTempPath(), "aictrl-tests", "transfers", Guid.NewGuid().ToString("N")));
}
