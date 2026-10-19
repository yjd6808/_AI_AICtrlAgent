using AiCtrl.Core.Domain;
using AiCtrl.Core.Providers;

namespace AiCtrl.Client.Systems;

public sealed class ProviderInstanceRegistry : IProviderInstanceRegistry
{
    private IReadOnlyList<IProviderInstance> _instances = Array.Empty<IProviderInstance>();

    public ProviderInstanceRegistry(MachineId machineId)
    {
        MachineId = machineId;
    }

    public MachineId MachineId { get; }

    public IReadOnlyList<IProviderInstance> Instances => Volatile.Read(ref _instances);

    public void Replace(IReadOnlyList<IProviderInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        Volatile.Write(ref _instances, instances);
    }
}
