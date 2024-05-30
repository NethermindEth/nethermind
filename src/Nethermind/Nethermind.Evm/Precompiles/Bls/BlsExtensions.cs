// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

public static class BlsParams
{
    public const int LenFr = 32;
    public const int LenFp = 64;
    public const int LenFpTrimmed = 48;
    public const int LenFpPad = LenFp - LenFpTrimmed;
    public const int LenG1 = 2 * LenFp;
    public const int LenG1Trimmed = 2 * LenFpTrimmed;
    public const int LenG2 = 4 * LenFp;
    public const int LenG2Trimmed = 4 * LenFpTrimmed;
    public static readonly byte[] BaseFieldOrder = [0x1a,0x01,0x11,0xea,0x39,0x7f,0xe6,0x9a,0x4b,0x1b,0xa7,0xb6,0x43,0x4b,0xac,0xd7,0x64,0x77,0x4b,0x84,0xf3,0x85,0x12,0xbf,0x67,0x30,0xd2,0xa0,0xf6,0xb0,0xf6,0x24,0x1e,0xab,0xff,0xfe,0xb1,0x53,0xff,0xff,0xb9,0xfe,0xff,0xff,0xff,0xff,0xaa,0xab];
}

public static class BlsExtensions
{
    public static G1? DecodeG1(in ReadOnlyMemory<byte> untrimmed)
    {
        if (untrimmed.Length != BlsParams.LenG1)
        {
            throw new Exception();
        }

        if (!ValidFp(untrimmed.Span[..BlsParams.LenFp]) || !ValidFp(untrimmed.Span[BlsParams.LenFp..]))
        {
            throw new Exception();
        }

        bool isInfinity = true;
        for (int i = 0; i < BlsParams.LenFpTrimmed; i++)
        {
            if (untrimmed.Span[BlsParams.LenFpPad + i] != 0 || untrimmed.Span[BlsParams.LenFp + BlsParams.LenFpPad + i] != 0)
            {
                isInfinity = false;
                break;
            }
        }

        if (isInfinity)
        {
            return null;
        }

        byte[] trimmed = new byte[BlsParams.LenG1Trimmed];
        untrimmed[BlsParams.LenFpPad..BlsParams.LenFp].CopyTo(trimmed);
        untrimmed[(BlsParams.LenFp + BlsParams.LenFpPad)..].CopyTo(trimmed.AsMemory()[BlsParams.LenFpTrimmed..]);

        G1 x = new(trimmed);
        if (!x.on_curve())
        {
            throw new Exception();
        }
        return x;
    }

    public static byte[] Encode(this G1 p)
    {
        if (p.is_inf())
        {
            return Enumerable.Repeat<byte>(0, 128).ToArray();
        }

        byte[] untrimmed = new byte[BlsParams.LenG1];
        Span<byte> trimmed = p.serialize();
        trimmed[..BlsParams.LenFpTrimmed].CopyTo(untrimmed.AsSpan()[BlsParams.LenFpPad..BlsParams.LenFp]);
        trimmed[BlsParams.LenFpTrimmed..].CopyTo(untrimmed.AsSpan()[(BlsParams.LenFp + BlsParams.LenFpPad)..]);
        return untrimmed;
    }

    public static G2? DecodeG2(in ReadOnlyMemory<byte> untrimmed)
    {
        if (untrimmed.Length != BlsParams.LenG2)
        {
            throw new Exception();
        }

        if (!ValidFp(untrimmed.Span[..BlsParams.LenFp]) ||
            !ValidFp(untrimmed.Span[BlsParams.LenFp..(2 * BlsParams.LenFp)]) ||
            !ValidFp(untrimmed.Span[(2 * BlsParams.LenFp)..(3 * BlsParams.LenFp)]) ||
            !ValidFp(untrimmed.Span[(3 * BlsParams.LenFp)..]))
        {
            throw new Exception();
        }

        bool isInfinity = true;
        for (int i = 0; i < BlsParams.LenFpTrimmed; i++)
        {
            if (untrimmed.Span[BlsParams.LenFpPad + i] != 0 ||
                untrimmed.Span[BlsParams.LenFp + BlsParams.LenFpPad + i] != 0 ||
                untrimmed.Span[(2 * BlsParams.LenFp) + BlsParams.LenFpPad + i] != 0 ||
                untrimmed.Span[(3 * BlsParams.LenFp) + BlsParams.LenFpPad + i] != 0
                )
            {
                isInfinity = false;
                break;
            }
        }

        if (isInfinity)
        {
            return null;
        }

        byte[] trimmed = new byte[BlsParams.LenG2Trimmed];
        untrimmed[BlsParams.LenFpPad..BlsParams.LenFp].CopyTo(trimmed.AsMemory()[BlsParams.LenFpTrimmed..]);
        untrimmed[(BlsParams.LenFp + BlsParams.LenFpPad)..(2 * BlsParams.LenFp)].CopyTo(trimmed.AsMemory());
        untrimmed[(2 * BlsParams.LenFp + BlsParams.LenFpPad)..(3 * BlsParams.LenFp)].CopyTo(trimmed.AsMemory()[(BlsParams.LenFpTrimmed * 3)..]);
        untrimmed[(3 * BlsParams.LenFp + BlsParams.LenFpPad)..].CopyTo(trimmed.AsMemory()[(BlsParams.LenFpTrimmed * 2)..]);

        G2 x = new(trimmed);
        if (!x.on_curve())
        {
            throw new Exception();
        }
        return x;
    }

    public static byte[] Encode(this G2 p)
    {
        if (p.is_inf())
        {
            return Enumerable.Repeat<byte>(0, 256).ToArray();
        }

        byte[] untrimmed = new byte[BlsParams.LenG2];
        Span<byte> trimmed = p.serialize();
        trimmed[..BlsParams.LenFpTrimmed].CopyTo(untrimmed.AsSpan()[(BlsParams.LenFp + BlsParams.LenFpPad)..]);
        trimmed[BlsParams.LenFpTrimmed..(2 * BlsParams.LenFpTrimmed)].CopyTo(untrimmed.AsSpan()[BlsParams.LenFpPad..BlsParams.LenFp]);
        trimmed[(2 * BlsParams.LenFpTrimmed)..(3 * BlsParams.LenFpTrimmed)].CopyTo(untrimmed.AsSpan()[(3 * BlsParams.LenFp + BlsParams.LenFpPad)..]);
        trimmed[(3 * BlsParams.LenFpTrimmed)..].CopyTo(untrimmed.AsSpan()[(2 * BlsParams.LenFp + BlsParams.LenFpPad)..]);
        return untrimmed;
    }

    public static bool ValidFp(ReadOnlySpan<byte> fp)
    {
        if (fp.Length != BlsParams.LenFp)
        {
            return false;
        }

        // check that padding bytes are zeroes
        for (int i = 0; i < BlsParams.LenFpPad; i++)
        {
            if (fp[i] != 0)
            {
                return false;
            }
        }

        // check that fp < base field order
        for (int i = 0; i < BlsParams.LenFpTrimmed; i++)
        {
            if (fp[i + BlsParams.LenFpTrimmed] > BlsParams.BaseFieldOrder[i])
            {
                return false;
            }
            else if (fp[i + BlsParams.LenFpTrimmed] < BlsParams.BaseFieldOrder[i])
            {
                return true;
            }
        }
        return false;
    }
}
