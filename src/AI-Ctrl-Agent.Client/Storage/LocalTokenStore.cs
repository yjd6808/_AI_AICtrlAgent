using System.Security.Cryptography;
using System.Text;

namespace AiCtrl.Client.Storage;

public sealed class LocalTokenStore
{
    private const int TokenBytes = 32;

    public LocalTokenStore(string tokenPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        TokenPath = tokenPath;
    }

    public string TokenPath { get; }

    public async Task<string> GetOrCreateAsync(CancellationToken ct)
    {
        if (File.Exists(TokenPath))
        {
            var existing = (await File.ReadAllTextAsync(TokenPath, ct).ConfigureAwait(false)).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var directory = Path.GetDirectoryName(TokenPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();

        try
        {
            await File.WriteAllTextAsync(TokenPath, token, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
                .ConfigureAwait(false);
            return token;
        }
        catch (IOException)
        {
            var existing = (await File.ReadAllTextAsync(TokenPath, ct).ConfigureAwait(false)).Trim();
            return existing.Length > 0 ? existing : token;
        }
    }
}
