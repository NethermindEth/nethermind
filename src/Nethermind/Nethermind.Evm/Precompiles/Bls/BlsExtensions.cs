// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

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
    public static G1 G1FromUntrimmed(in ReadOnlyMemory<byte> untrimmed)
    {
        if (untrimmed.Length != BlsParams.LenG1)
        {
            throw new Exception();
        }

        byte[] trimmed = new byte[BlsParams.LenG1Trimmed];
        untrimmed[BlsParams.LenFpPad..BlsParams.LenFp].CopyTo(trimmed);
        untrimmed[(BlsParams.LenFp + BlsParams.LenFpPad)..].CopyTo(trimmed.AsMemory()[BlsParams.LenFpTrimmed..]);
        return new(trimmed);
    }

    public static byte[] ToBytesUntrimmed(this G1 p)
    {
        byte[] untrimmed = new byte[BlsParams.LenG1];
        Span<byte> trimmed = p.serialize();
        trimmed[..BlsParams.LenFpTrimmed].CopyTo(untrimmed.AsSpan()[BlsParams.LenFpPad..BlsParams.LenFp]);
        trimmed[BlsParams.LenFpTrimmed..].CopyTo(untrimmed.AsSpan()[(BlsParams.LenFp + BlsParams.LenFpPad)..]);
        return untrimmed;
    }

    public static G2 G2FromUntrimmed(in ReadOnlyMemory<byte> untrimmed)
    {
        if (untrimmed.Length != BlsParams.LenG2)
        {
            throw new Exception();
        }

        byte[] trimmed = new byte[BlsParams.LenG2Trimmed];
        untrimmed[BlsParams.LenFpPad..BlsParams.LenFp].CopyTo(trimmed.AsMemory());
        untrimmed[(BlsParams.LenFp + BlsParams.LenFpPad)..(2 * BlsParams.LenFp)].CopyTo(trimmed.AsMemory()[BlsParams.LenFpTrimmed..]);
        untrimmed[(2 * BlsParams.LenFp + BlsParams.LenFpPad)..(3 * BlsParams.LenFp)].CopyTo(trimmed.AsMemory()[(BlsParams.LenFpTrimmed * 2)..]);
        untrimmed[(3 * BlsParams.LenFp + BlsParams.LenFpPad)..].CopyTo(trimmed.AsMemory()[(BlsParams.LenFpTrimmed * 3)..]);
        return new(trimmed);
    }

    public static byte[] ToBytesUntrimmed(this G2 p)
    {
        byte[] untrimmed = new byte[BlsParams.LenG2];
        Span<byte> trimmed = p.serialize();
        trimmed[..BlsParams.LenFpTrimmed].CopyTo(untrimmed.AsSpan()[BlsParams.LenFpPad..BlsParams.LenFp]);
        trimmed[BlsParams.LenFpTrimmed..(2 * BlsParams.LenFpTrimmed)].CopyTo(untrimmed.AsSpan()[(BlsParams.LenFp + BlsParams.LenFpPad)..]);
        trimmed[(2 * BlsParams.LenFpTrimmed)..(3 * BlsParams.LenFpTrimmed)].CopyTo(untrimmed.AsSpan()[(2 * BlsParams.LenFp + BlsParams.LenFpPad)..]);
        trimmed[(3 * BlsParams.LenFpTrimmed)..].CopyTo(untrimmed.AsSpan()[(3 * BlsParams.LenFp + BlsParams.LenFpPad)..]);
        return untrimmed;
    }
}
