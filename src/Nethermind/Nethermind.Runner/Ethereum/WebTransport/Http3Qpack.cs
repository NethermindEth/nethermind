// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using Nethermind.Core.Collections;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal static class Http3Qpack
{
    // RFC 7541 Appendix B, with a leading 1 marking each code's bit length.
    private static readonly uint[] HuffmanCodes =
    [
        0x3ff8, 0xffffd8, 0x1fffffe2, 0x1fffffe3, 0x1fffffe4, 0x1fffffe5, 0x1fffffe6, 0x1fffffe7,
        0x1fffffe8, 0x1ffffea, 0x7ffffffc, 0x1fffffe9, 0x1fffffea, 0x7ffffffd, 0x1fffffeb, 0x1fffffec,
        0x1fffffed, 0x1fffffee, 0x1fffffef, 0x1ffffff0, 0x1ffffff1, 0x1ffffff2, 0x7ffffffe, 0x1ffffff3,
        0x1ffffff4, 0x1ffffff5, 0x1ffffff6, 0x1ffffff7, 0x1ffffff8, 0x1ffffff9, 0x1ffffffa, 0x1ffffffb,
        0x54, 0x7f8, 0x7f9, 0x1ffa, 0x3ff9, 0x55, 0x1f8, 0xffa,
        0x7fa, 0x7fb, 0x1f9, 0xffb, 0x1fa, 0x56, 0x57, 0x58,
        0x20, 0x21, 0x22, 0x59, 0x5a, 0x5b, 0x5c, 0x5d,
        0x5e, 0x5f, 0xdc, 0x1fb, 0xfffc, 0x60, 0x1ffb, 0x7fc,
        0x3ffa, 0x61, 0xdd, 0xde, 0xdf, 0xe0, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea,
        0xeb, 0xec, 0xed, 0xee, 0xef, 0xf0, 0xf1, 0xf2,
        0x1fc, 0xf3, 0x1fd, 0x3ffb, 0xffff0, 0x3ffc, 0x7ffc, 0x62,
        0xfffd, 0x23, 0x63, 0x24, 0x64, 0x25, 0x65, 0x66,
        0x67, 0x26, 0xf4, 0xf5, 0x68, 0x69, 0x6a, 0x27,
        0x6b, 0xf6, 0x6c, 0x28, 0x29, 0x6d, 0xf7, 0xf8,
        0xf9, 0xfa, 0xfb, 0xfffe, 0xffc, 0x7ffd, 0x3ffd, 0x1ffffffc,
        0x1fffe6, 0x7fffd2, 0x1fffe7, 0x1fffe8, 0x7fffd3, 0x7fffd4, 0x7fffd5, 0xffffd9,
        0x7fffd6, 0xffffda, 0xffffdb, 0xffffdc, 0xffffdd, 0xffffde, 0x1ffffeb, 0xffffdf,
        0x1ffffec, 0x1ffffed, 0x7fffd7, 0xffffe0, 0x1ffffee, 0xffffe1, 0xffffe2, 0xffffe3,
        0xffffe4, 0x3fffdc, 0x7fffd8, 0xffffe5, 0x7fffd9, 0xffffe6, 0xffffe7, 0x1ffffef,
        0x7fffda, 0x3fffdd, 0x1fffe9, 0x7fffdb, 0x7fffdc, 0xffffe8, 0xffffe9, 0x3fffde,
        0xffffea, 0x7fffdd, 0x7fffde, 0x1fffff0, 0x3fffdf, 0x7fffdf, 0xffffeb, 0xffffec,
        0x3fffe0, 0x3fffe1, 0x7fffe0, 0x3fffe2, 0xffffed, 0x7fffe1, 0xffffee, 0xffffef,
        0x1fffea, 0x7fffe2, 0x7fffe3, 0x7fffe4, 0xfffff0, 0x7fffe5, 0x7fffe6, 0xfffff1,
        0x7ffffe0, 0x7ffffe1, 0x1fffeb, 0xffff1, 0x7fffe7, 0xfffff2, 0x7fffe8, 0x3ffffec,
        0x7ffffe2, 0x7ffffe3, 0x7ffffe4, 0xfffffde, 0xfffffdf, 0x7ffffe5, 0x1fffff1, 0x3ffffed,
        0xffff2, 0x3fffe3, 0x7ffffe6, 0xfffffe0, 0xfffffe1, 0x7ffffe7, 0xfffffe2, 0x1fffff2,
        0x3fffe4, 0x3fffe5, 0x7ffffe8, 0x7ffffe9, 0x1ffffffd, 0xfffffe3, 0xfffffe4, 0xfffffe5,
        0x1fffec, 0x1fffff3, 0x1fffed, 0x3fffe6, 0x7fffe9, 0x3fffe7, 0x3fffe8, 0xfffff3,
        0x7fffea, 0x7fffeb, 0x3ffffee, 0x3ffffef, 0x1fffff4, 0x1fffff5, 0x7ffffea, 0xfffff4,
        0x7ffffeb, 0xfffffe6, 0x7ffffec, 0x7ffffed, 0xfffffe7, 0xfffffe8, 0xfffffe9, 0xfffffea,
        0xfffffeb, 0x1ffffffe, 0xfffffec, 0xfffffed, 0xfffffee, 0xfffffef, 0xffffff0, 0x7ffffee,
        0x7fffffff,
    ];

    private static readonly short[] HuffmanTree = BuildHuffmanTree();

    internal static bool TryReadOffer(ReadOnlySpan<byte> encoded, [NotNullWhen(true)] out string? offer)
    {
        offer = null;
        // The server advertises zero dynamic capacity and acknowledges no inserts.
        if (!TryReadInteger(ref encoded, 8, out int insertCount) || insertCount != 0 ||
            encoded.IsEmpty || (encoded[0] & 0x80) != 0 || !TryReadInteger(ref encoded, 7, out _))
            return false;
        string? method = null, scheme = null, path = null, authority = null, protocol = null;
        bool regularHeaders = false;
        string? draft02 = null, origin = null;
        while (!encoded.IsEmpty)
        {
            if (!TryReadField(ref encoded, out string? name, out string? value))
                return false;

            if (!name.StartsWith(':'))
            {
                if (name is "connection" or "keep-alive" or "proxy-connection" or "transfer-encoding" or "upgrade" ||
                    (name == "te" && !value.Trim().Equals("trailers", StringComparison.OrdinalIgnoreCase)))
                    return false;

                regularHeaders = true;
                if (name == "wt-available-protocols")
                    offer = offer is null ? value : string.Concat(offer, ", ", value);
                else if (name == "sec-webtransport-http3-draft02")
                {
                    if (draft02 is not null)
                        return false;
                    draft02 = value;
                }
                else if (name == "origin")
                {
                    if (origin is not null)
                        return false;
                    origin = value;
                }
                continue;
            }

            if (regularHeaders)
                return false;

            switch (name)
            {
                case ":method" when method is null:
                    method = value;
                    break;
                case ":scheme" when scheme is null:
                    scheme = value;
                    break;
                case ":path" when path is null:
                    path = value;
                    break;
                case ":authority" when authority is null:
                    authority = value;
                    break;
                case ":protocol" when protocol is null:
                    protocol = value;
                    break;
                default:
                    return false;
            }
        }

        // The public peer endpoint accepts signed transactions and peer queries from any origin.
        return method == "CONNECT" && scheme == "https" && path == "/devp2p" &&
               !string.IsNullOrEmpty(authority) && protocol == "webtransport" && draft02 == "1" &&
               !string.IsNullOrWhiteSpace(origin) && !string.IsNullOrEmpty(offer);
    }

    private static bool TryReadField(ref ReadOnlySpan<byte> encoded, [NotNullWhen(true)] out string? name, [NotNullWhen(true)] out string? value)
    {
        name = value = null;
        byte first = encoded[0];
        if ((first & 0x80) != 0)
        {
            if ((first & 0x40) == 0 || !TryReadInteger(ref encoded, 6, out int index) || index >= 99)
                return false;
            name = StaticName(index);
            value = index switch
            {
                1 => "/",
                15 => "CONNECT",
                23 => "https",
                _ => string.Empty,
            };
            return true;
        }

        if ((first & 0x40) != 0)
        {
            if ((first & 0x10) == 0 || !TryReadInteger(ref encoded, 4, out int index) || index >= 99)
                return false;
            name = StaticName(index);
        }
        else if ((first & 0x20) == 0 || !TryReadString(ref encoded, 3, out name) || !IsFieldName(name))
        {
            // Post-base references also require the dynamic table.
            return false;
        }

        return TryReadString(ref encoded, 7, out value);
    }

    private static string StaticName(int index) => index switch
    {
        0 => ":authority",
        1 => ":path",
        >= 15 and <= 21 => ":method",
        22 or 23 => ":scheme",
        >= 24 and <= 28 or >= 63 and <= 71 => ":status",
        90 => "origin",
        _ => string.Empty,
    };

    private static bool IsFieldName(string name)
    {
        ReadOnlySpan<char> body = name.StartsWith(':') ? name.AsSpan(1) : name;
        if (body.IsEmpty)
            return false;

        foreach (char c in body)
        {
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '!' or '#' or '$' or '%' or
                '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '\x60' or '|' or '~'))
                return false;
        }

        return true;
    }

    private static bool TryReadInteger(ref ReadOnlySpan<byte> encoded, int prefixBits, out int value)
    {
        value = 0;
        if (encoded.IsEmpty)
            return false;

        int mask = (1 << prefixBits) - 1;
        value = encoded[0] & mask;
        encoded = encoded[1..];
        if (value != mask)
            return true;

        for (int shift = 0; shift <= 21 && !encoded.IsEmpty; shift += 7)
        {
            byte next = encoded[0];
            encoded = encoded[1..];
            value += (next & 0x7f) << shift;
            if ((next & 0x80) == 0)
                return true;
        }

        return false;
    }

    private static bool TryReadString(ref ReadOnlySpan<byte> encoded, int prefixBits, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (encoded.IsEmpty)
            return false;

        bool huffman = (encoded[0] & (1 << prefixBits)) != 0;
        if (!TryReadInteger(ref encoded, prefixBits, out int length) || length > encoded.Length)
            return false;

        ReadOnlySpan<byte> bytes = encoded[..length];
        encoded = encoded[length..];
        if (!huffman)
        {
            if (bytes.IndexOfAny((byte)0, (byte)'\r', (byte)'\n') >= 0)
                return false;
            value = Encoding.Latin1.GetString(bytes);
            return true;
        }

        using ArrayPoolSpan<byte> decoded = new(length * 8 / 5);
        int written = 0, node = 0, padding = 0, pendingBits = 0;
        foreach (byte next in bytes)
        {
            for (int shift = 7; shift >= 0; shift--)
            {
                int bit = (next >> shift) & 1;
                node = HuffmanTree[(node * 2) + bit];
                padding = (padding << 1) | bit;
                pendingBits++;
                if (node >= 0)
                    continue;

                int symbol = ~node;
                if (symbol is 0 or '\r' or '\n' or 256)
                    return false;

                decoded[written++] = (byte)symbol;
                node = padding = pendingBits = 0;
            }
        }

        if (pendingBits > 7 || padding != (1 << pendingBits) - 1)
            return false;

        value = Encoding.Latin1.GetString(decoded.Slice(0, written));
        return true;
    }

    private static short[] BuildHuffmanTree()
    {
        short[] tree = new short[HuffmanCodes.Length * 2];
        short last = 0;
        for (int symbol = 0; symbol < HuffmanCodes.Length; symbol++)
        {
            uint code = HuffmanCodes[symbol];
            int node = 0;
            for (int bit = BitOperations.Log2(code) - 1; bit > 0; bit--)
            {
                int branch = node * 2 + (int)((code >> bit) & 1);
                if (tree[branch] == 0)
                    tree[branch] = ++last;
                node = tree[branch];
            }

            tree[(node * 2) + (int)(code & 1)] = (short)~symbol;
        }

        return tree;
    }
}
