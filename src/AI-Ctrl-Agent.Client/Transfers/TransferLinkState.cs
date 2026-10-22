using AiCtrl.Client.Link;

namespace AiCtrl.Client.Transfers;

public sealed class TransferLinkState
{
    private ServerApiClient? _api;

    private string? _token;

    public void Set(ServerApiClient? api, string? token)
    {
        _api = api;
        _token = token;
    }

    public bool TryGet(out ServerApiClient api, out string token)
    {
        var currentApi = _api;
        var currentToken = _token;
        if (currentApi is null || string.IsNullOrWhiteSpace(currentToken))
        {
            api = null!;
            token = string.Empty;
            return false;
        }

        api = currentApi;
        token = currentToken;
        return true;
    }
}
