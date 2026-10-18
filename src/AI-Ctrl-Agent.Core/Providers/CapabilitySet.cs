namespace AiCtrl.Core.Providers;

[Flags]
public enum CapabilitySet
{
    None = 0,
    ListSessions = 1,
    ReadMessages = 2,
    Abort = 4,
    Prompt = 8,
    PromptAsync = 16,
    ModelSelect = 32,
    Permissions = 64,
    LiveEvents = 128,
    SubSessions = 256,
}
