// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network.Rlpx;
using Snappier;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal static class BrowserFrames
{
    private const string NoncePrefix = "ENR-key-proof-v1=";
    private const int HeaderLength = 4;
    private const byte RequestCode = 0x10;
    private const byte ResponseCode = 0x11;
    private const int MaxResponseLength = 8 * 1024;

    // WT-Available-Protocols is a Structured Fields list of strings. Its parameters have no semantics.
    internal static (byte Mode, byte[] Nonce)? ParseOffer(string value)
    {
        if (value.Length > 4096)
            return null;

        Span<byte> nonce = stackalloc byte[BrowserProtocol.NonceLength];
        bool hasNonce = false;
        byte mode = 0;
        ReadOnlySpan<char> remaining = value.AsSpan().TrimStart(" \t");
        while (true)
        {
            if (!TryReadString(ref remaining, out ReadOnlySpan<char> item) || !TrySkipParameters(ref remaining))
                return null;

            if (item.SequenceEqual("webtx/1"))
            {
                mode = BrowserProtocol.TxMode;
            }
            else if (mode == 0 && item.SequenceEqual("webdisc/1"))
            {
                mode = BrowserProtocol.DiscoveryMode;
            }
            else if (item.StartsWith(NoncePrefix, StringComparison.Ordinal))
            {
                ReadOnlySpan<char> encoded = item[NoncePrefix.Length..];
                if (hasNonce || encoded.Length != nonce.Length * 2 ||
                    Convert.FromHexString(encoded, nonce, out _, out _) != OperationStatus.Done)
                    return null;

                hasNonce = true;
            }

            remaining = remaining.TrimStart(" \t");
            if (remaining.IsEmpty)
                return mode != 0 && hasNonce ? (mode, nonce.ToArray()) : null;
            if (remaining[0] != ',')
                return null;

            remaining = remaining[1..].TrimStart(" \t");
        }
    }

    internal static async ValueTask<(IMemoryOwner<byte> Owner, int Length)> ReadRequestAsync(
        Stream stream, byte mode, CancellationToken cancellationToken)
    {
        int limit = mode switch
        {
            BrowserProtocol.TxMode => BrowserProtocol.MaxSubmitSize,
            BrowserProtocol.DiscoveryMode => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        byte[] header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int encodedLength = header[1] << 16 | header[2] << 8 | header[3];
        if (header[0] != RequestCode || encodedLength == 0 || encodedLength > Snappy.GetMaxCompressedLength(limit))
            throw new InvalidDataException("Invalid browser WebTransport request frame.");

        using IMemoryOwner<byte> compressed = MemoryPool<byte>.Shared.Rent(encodedLength);
        Memory<byte> encoded = compressed.Memory[..encodedLength];
        await stream.ReadExactlyAsync(encoded, cancellationToken);
        int decodedLength = Snappy.GetUncompressedLength(encoded.Span);
        if (decodedLength <= 0 || decodedLength > limit || !SnappyBlockValidator.IsValid(encoded.Span, decodedLength))
            throw new InvalidDataException("Invalid browser WebTransport Snappy block.");

        IMemoryOwner<byte> decoded = MemoryPool<byte>.Shared.Rent(decodedLength);
        try
        {
            Span<byte> payload = decoded.Memory.Span[..decodedLength];
            Snappy.Decompress(encoded.Span, payload);
            if (mode == BrowserProtocol.DiscoveryMode && payload[0] != 0xc0)
                throw new InvalidDataException("The browser discovery request does not contain an empty RLP list.");

            return (decoded, decodedLength);
        }
        catch
        {
            decoded.Dispose();
            throw;
        }
    }

    internal static async ValueTask WriteResponseAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > MaxResponseLength)
            throw new ArgumentOutOfRangeException(nameof(payload));

        using IMemoryOwner<byte> buffer = MemoryPool<byte>.Shared.Rent(HeaderLength + Snappy.GetMaxCompressedLength(payload.Length));
        int length = Snappy.Compress(payload, buffer.Memory.Span[HeaderLength..]);
        Span<byte> header = buffer.Memory.Span[..HeaderLength];
        header[0] = ResponseCode;
        header[1] = (byte)(length >> 16);
        header[2] = (byte)(length >> 8);
        header[3] = (byte)length;
        await stream.WriteAsync(buffer.Memory[..(HeaderLength + length)], cancellationToken);
    }

    private static bool TryReadString(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> item)
    {
        item = default;
        if (remaining.IsEmpty || remaining[0] != '"')
            return false;

        for (int i = 1; i < remaining.Length; i++)
        {
            char current = remaining[i];
            if (current == '"')
            {
                item = remaining[1..i];
                remaining = remaining[(i + 1)..];
                return true;
            }

            if (current is < ' ' or > '~' ||
                (current == '\\' && (++i == remaining.Length || remaining[i] is not ('"' or '\\'))))
                return false;
        }

        return false;
    }

    private static bool TrySkipParameters(ref ReadOnlySpan<char> remaining)
    {
        while (!remaining.IsEmpty && remaining[0] == ';')
        {
            remaining = remaining[1..].TrimStart(' ');
            if (remaining.IsEmpty || remaining[0] is not (>= 'a' and <= 'z' or '*'))
                return false;

            int keyLength = 1;
            while (keyLength < remaining.Length && remaining[keyLength] is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' or '*')
                keyLength++;

            remaining = remaining[keyLength..];
            if (!remaining.IsEmpty && remaining[0] == '=')
            {
                remaining = remaining[1..];
                if (!TrySkipParameterValue(ref remaining))
                    return false;
            }
        }

        return true;
    }

    private static bool TrySkipParameterValue(ref ReadOnlySpan<char> remaining)
    {
        if (remaining.IsEmpty)
            return false;
        if (remaining[0] == '"')
            return TryReadString(ref remaining, out _);
        if (remaining[0] == '%')
            return TrySkipDisplayString(ref remaining);

        int separator = remaining.IndexOfAny(",; \t");
        ReadOnlySpan<char> item = separator < 0 ? remaining : remaining[..separator];
        if (item.IsEmpty)
            return false;

        remaining = remaining[item.Length..];
        return item[0] switch
        {
            ':' => IsByteSequence(item),
            '?' => item.Length == 2 && item[1] is '0' or '1',
            '@' => IsNumber(item[1..], decimalAllowed: false),
            '-' or >= '0' and <= '9' => IsNumber(item, decimalAllowed: true),
            >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '*' => IsToken(item),
            _ => false,
        };
    }

    private static bool IsNumber(ReadOnlySpan<char> item, bool decimalAllowed)
    {
        if (!item.IsEmpty && item[0] == '-')
            item = item[1..];

        int point = item.IndexOf('.');
        return point < 0
            ? IsDigits(item, 15)
            : decimalAllowed && IsDigits(item[..point], 12) && IsDigits(item[(point + 1)..], 3);

        static bool IsDigits(ReadOnlySpan<char> digits, int limit) =>
            digits.Length > 0 && digits.Length <= limit && digits.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool IsToken(ReadOnlySpan<char> item)
    {
        foreach (char value in item[1..])
        {
            if (!char.IsAsciiLetterOrDigit(value) && !"!#$%&'*+-.^_`|~:/".Contains(value))
                return false;
        }

        return true;
    }

    private static bool IsByteSequence(ReadOnlySpan<char> item)
    {
        if (item.Length < 2 || item[^1] != ':')
            return false;

        ReadOnlySpan<char> base64 = item[1..^1];
        foreach (char value in base64)
        {
            if (!char.IsAsciiLetterOrDigit(value) && value is not ('+' or '/' or '='))
                return false;
        }

        int padding = (4 - base64.Length % 4) % 4;
        if (padding == 0)
            return Convert.TryFromBase64Chars(base64, stackalloc byte[base64.Length], out _);

        Span<char> padded = stackalloc char[base64.Length + padding];
        base64.CopyTo(padded);
        padded[base64.Length..].Fill('=');
        return Convert.TryFromBase64Chars(padded, stackalloc byte[padded.Length], out _);
    }

    private static bool TrySkipDisplayString(ref ReadOnlySpan<char> remaining)
    {
        if (remaining.Length < 2 || remaining[1] != '"')
            return false;

        Span<byte> decoded = stackalloc byte[remaining.Length];
        int written = 0;
        for (int i = 2; i < remaining.Length; i++)
        {
            char current = remaining[i];
            if (current is < ' ' or > '~')
                return false;
            if (current == '"')
            {
                remaining = remaining[(i + 1)..];
                return Utf8.IsValid(decoded[..written]);
            }
            if (current == '%')
            {
                if (remaining.Length - i < 3 || !IsLowerHex(remaining[i + 1]) || !IsLowerHex(remaining[i + 2]))
                    return false;

                Convert.FromHexString(remaining.Slice(i + 1, 2), decoded.Slice(written, 1), out _, out _);
                i += 2;
            }
            else
            {
                decoded[written] = (byte)current;
            }

            written++;
        }

        return false;

        static bool IsLowerHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';
    }
}
