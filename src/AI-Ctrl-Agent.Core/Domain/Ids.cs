namespace AiCtrl.Core.Domain;

public readonly record struct MachineId(string Value);

public readonly record struct InstanceId(string Value);

public readonly record struct SessionRef(MachineId Machine, InstanceId Instance, string SessionId);
