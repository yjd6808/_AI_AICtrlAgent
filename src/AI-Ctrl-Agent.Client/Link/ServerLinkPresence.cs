namespace AiCtrl.Client.Link;

public sealed class ServerLinkPresence
{
    private int _connected;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public void Set(bool connected) => Volatile.Write(ref _connected, connected ? 1 : 0);
}
