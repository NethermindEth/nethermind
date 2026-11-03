// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Paprika.Crypto;

public static class SpanExtensions
{
    public static string ToHexString(this in Span<byte> span, bool withZeroX)
    {
        return ToHexString(span, withZeroX, false, false);
    }

    public static string ToHexString(this in ReadOnlySpan<byte> span, bool withZeroX)
    {
        return ToHexString(span, withZeroX, false, false);
    }

    private static string ToHexString(this in ReadOnlySpan<byte> span, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
    {
        return ToHexViaLookup(span, withZeroX, noLeadingZeros);
    }

    [DebuggerStepThrough]
    private static string ToHexViaLookup(in ReadOnlySpan<byte> span, bool withZeroX, bool skipLeadingZeros)
    {
        int leadingZeros = skipLeadingZeros ? CountLeadingZeros(span) : 0;
        char[] result = new char[span.Length * 2 + (withZeroX ? 2 : 0) - leadingZeros];

        if (withZeroX)
        {
            result[0] = '0';
            result[1] = 'x';
        }

        for (int i = 0; i < span.Length; i++)
        {
            uint val = Lookup32[span[i]];
            char char1 = (char)val;
            char char2 = (char)(val >> 16);

            if (leadingZeros <= i * 2)
            {
                result[2 * i + (withZeroX ? 2 : 0) - leadingZeros] = char1;
            }

            if (leadingZeros <= i * 2 + 1)
            {
                result[2 * i + 1 + (withZeroX ? 2 : 0) - leadingZeros] = char2;
            }
        }

        if (skipLeadingZeros && result.Length == (withZeroX ? 2 : 0))
        {
            return withZeroX ? "0x0" : "0";
        }

        return new string(result);
    }

    private static readonly uint[] Lookup32 = CreateLookup32("x2");

    private static uint[] CreateLookup32(string format)
    {
        uint[] result = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            string s = i.ToString(format);
            result[i] = s[0] + ((uint)s[1] << 16);
        }

        return result;
    }

    private static int CountLeadingZeros(in ReadOnlySpan<byte> span)
    {
        int leadingZeros = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if ((span[i] & 240) == 0)
            {
                leadingZeros++;
                if ((span[i] & 15) == 0)
                {
                    leadingZeros++;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return leadingZeros;
    }
}
