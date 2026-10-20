using System.Security.Cryptography;

namespace AiCtrl.Core.Protocol;

public static class RequestIds
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        var timestamp = (ulong)TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        Span<char> value = stackalloc char[26];
        for (var index = 9; index >= 0; index--)
        {
            value[index] = Alphabet[(int)(timestamp & 31)];
            timestamp >>= 5;
        }

        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        var buffer = 0;
        var bits = 0;
        var output = 10;
        foreach (var item in random)
        {
            buffer = (buffer << 8) | item;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                value[output++] = Alphabet[(buffer >> bits) & 31];
            }
        }

        return new string(value);
    }

    public static bool IsValid(string? value) =>
        value is { Length: 26 } && value.All(character => Alphabet.Contains(character));
}
