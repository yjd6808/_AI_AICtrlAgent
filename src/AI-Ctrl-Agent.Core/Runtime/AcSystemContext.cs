using Microsoft.Extensions.Logging;

namespace AiCtrl.Core.Runtime;

public sealed record AcSystemContext(
    IServiceProvider Services,
    IAcEventBus Bus,
    TimeProvider Time,
    ILoggerFactory Logs,
    AcLoopSupervisor Loops);
