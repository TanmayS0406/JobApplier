using System.Security.Cryptography;

namespace JobApplier.Infrastructure;

/// <summary>
/// Generates one strong, unique password per job site. 16 characters fits the shortest maximum length that common
/// portals enforce, and the symbol set avoids characters that forms and URLs tend to mangle.
/// </summary>
public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // no I, O
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";  // no l
    private const string Digits = "23456789";                  // no 0, 1
    private const string Symbols = "!@%&*?"; // no # or $ - they are special in dotenv files and shells

    public static string Generate(int length = 16)
    {
        if (length < 12) throw new ArgumentOutOfRangeException(nameof(length), "Use at least 12 characters.");

        var chars = new List<char>(length)
        {
            Pick(Upper), Pick(Upper),
            Pick(Lower), Pick(Lower),
            Pick(Digits), Pick(Digits),
            Pick(Symbols), Pick(Symbols)
        };

        var all = Upper + Lower + Digits + Symbols;
        while (chars.Count < length) chars.Add(Pick(all));

        // Fisher-Yates so the guaranteed characters are not always at the front.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
