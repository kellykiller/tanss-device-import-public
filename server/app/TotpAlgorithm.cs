using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TnsApiImport;

internal static class TotpAlgorithm
{
    private const int TimeStepSeconds = 30;

    internal static bool TryVerify(
        byte[] secret,
        string? code,
        DateTimeOffset timestamp,
        out long matchedCounter)
    {
        matchedCounter = 0;

        if (string.IsNullOrWhiteSpace(code) ||
            code.Length != 6 ||
            code.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        var currentCounter = timestamp.ToUnixTimeSeconds() / TimeStepSeconds;
        var suppliedCode = Encoding.ASCII.GetBytes(code);

        try
        {
            for (var offset = -1; offset <= 1; offset++)
            {
                var candidateCounter = currentCounter + offset;
                var expectedCode = Encoding.ASCII.GetBytes(
                    GenerateCode(secret, candidateCounter, 6));

                try
                {
                    if (CryptographicOperations.FixedTimeEquals(
                            suppliedCode,
                            expectedCode))
                    {
                        matchedCounter = candidateCounter;
                        return true;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedCode);
                }
            }

            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedCode);
        }
    }

    internal static string GenerateCode(byte[] secret, long counter, int digits)
    {
        if (digits is < 6 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(digits),
                "TOTP-Codes müssen zwischen 6 und 8 Stellen besitzen.");
        }

        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes.ToArray());

        try
        {
            var offset = hash[^1] & 0x0F;
            var binaryCode =
                ((hash[offset] & 0x7F) << 24) |
                ((hash[offset + 1] & 0xFF) << 16) |
                ((hash[offset + 2] & 0xFF) << 8) |
                (hash[offset + 3] & 0xFF);
            var divisor = digits switch
            {
                6 => 1_000_000,
                7 => 10_000_000,
                8 => 100_000_000,
                _ => throw new InvalidOperationException()
            };

            return (binaryCode % divisor).ToString(
                new string('0', digits),
                CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }
}

internal static class Base32Encoding
{
    internal static byte[] Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Der TOTP-Schlüssel ist leer.");
        }

        var normalized = string.Concat(
            value.Where(character => !char.IsWhiteSpace(character) && character != '='))
            .ToUpperInvariant();

        var output = new List<byte>((normalized.Length * 5) / 8);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var character in normalized)
        {
            var valuePart = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => throw new InvalidOperationException(
                    "Der TOTP-Schlüssel enthält ungültige Base32-Zeichen.")
            };

            buffer = (buffer << 5) | valuePart;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.Add((byte)(buffer >> bitsInBuffer));
                buffer &= (1 << bitsInBuffer) - 1;
            }
        }

        return output.ToArray();
    }
}
