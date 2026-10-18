namespace AiCtrl.Core.Domain;

public sealed record MachineFeature(
    string Key,
    FeatureSection Section,
    string Title,
    string Route,
    bool Available,
    string? UnavailableReason,
    string? Badge);
