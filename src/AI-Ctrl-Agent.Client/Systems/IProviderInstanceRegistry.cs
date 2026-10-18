using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;

namespace AiCtrl.Client.Systems;

public interface IProviderInstanceRegistry
{
    MachineId MachineId { get; }

    IReadOnlyList<IProviderInstance> Instances { get; }
}
