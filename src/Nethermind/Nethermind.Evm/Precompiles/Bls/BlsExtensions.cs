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
}

public static class BlsExtensions
{
    public static G1? DecodeG1(in ReadOnlyMemory<byte> untrimmed)
    {
        if (untrimmed.Length != BlsParams.LenG1)
        {
            throw new Exception();
        }

        for (int i = 0; i < BlsParams.LenFpPad; i++)
        {
            if (untrimmed.Span[i] != 0 || untrimmed.Span[BlsParams.LenFp + i] != 0)
            {
                throw new Exception();
            }
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

        for (int i = 0; i < BlsParams.LenFpPad; i++)
        {
            if (untrimmed.Span[i] != 0 ||
                untrimmed.Span[BlsParams.LenFp + i] != 0 ||
                untrimmed.Span[(2 * BlsParams.LenFp) + i] != 0 ||
                untrimmed.Span[(3 * BlsParams.LenFp) + i] != 0
                )
            {
                throw new Exception();
            }
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
}
