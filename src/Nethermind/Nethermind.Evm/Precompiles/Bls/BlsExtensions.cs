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
    public static readonly byte[] BaseFieldOrder = [0x1a, 0x01, 0x11, 0xea, 0x39, 0x7f, 0xe6, 0x9a, 0x4b, 0x1b, 0xa7, 0xb6, 0x43, 0x4b, 0xac, 0xd7, 0x64, 0x77, 0x4b, 0x84, 0xf3, 0x85, 0x12, 0xbf, 0x67, 0x30, 0xd2, 0xa0, 0xf6, 0xb0, 0xf6, 0x24, 0x1e, 0xab, 0xff, 0xfe, 0xb1, 0x53, 0xff, 0xff, 0xb9, 0xfe, 0xff, 0xff, 0xff, 0xff, 0xaa, 0xab];
}

public static class BlsExtensions
{
    public class BlsPrecompileException(string message, Exception? innerException = null) : Exception(message, innerException);

    public static void DecodeRaw(this G1 p, ReadOnlySpan<byte> raw)
    {
        if (raw.Length != BlsParams.LenG1)
        {
            throw new BlsPrecompileException("G1 point was wrong size.");
        }

        if (!ValidRawFp(raw[..BlsParams.LenFp]) || !ValidRawFp(raw[BlsParams.LenFp..]))
        {
            throw new BlsPrecompileException("Field point was invalid.");
        }

        p.Point.Clear();

        ReadOnlySpan<byte> fp0 = raw[BlsParams.LenFpPad..BlsParams.LenFp];
        ReadOnlySpan<byte> fp1 = raw[(BlsParams.LenFp + BlsParams.LenFpPad)..];

        if (!fp0.ContainsAnyExcept((byte)0) && !fp1.ContainsAnyExcept((byte)0))
        {
            return;
        }

        p.Decode(fp0, fp1);
        if (!p.OnCurve())
        {
            throw new BlsPrecompileException("G1 point not on curve.");
        }
    }

    public static byte[] EncodeRaw(this G1 p)
    {
        if (p.IsInf())
        {
            return Enumerable.Repeat<byte>(0, 128).ToArray();
        }

        byte[] raw = new byte[BlsParams.LenG1];
        ReadOnlySpan<byte> trimmed = p.Serialize();
        trimmed[..BlsParams.LenFpTrimmed].CopyTo(raw.AsSpan()[BlsParams.LenFpPad..BlsParams.LenFp]);
        trimmed[BlsParams.LenFpTrimmed..].CopyTo(raw.AsSpan()[(BlsParams.LenFp + BlsParams.LenFpPad)..]);
        return raw;
    }

    public static void DecodeRaw(this G2 p, ReadOnlySpan<byte> raw)
    {
        if (raw.Length != BlsParams.LenG2)
        {
            throw new BlsPrecompileException("G2 point was wrong size.");
        }

        if (!ValidRawFp(raw[..BlsParams.LenFp]) ||
            !ValidRawFp(raw[BlsParams.LenFp..(2 * BlsParams.LenFp)]) ||
            !ValidRawFp(raw[(2 * BlsParams.LenFp)..(3 * BlsParams.LenFp)]) ||
            !ValidRawFp(raw[(3 * BlsParams.LenFp)..]))
        {
            throw new BlsPrecompileException("Field point was invalid.");
        }

        p.Point.Clear();

        ReadOnlySpan<byte> fp0 = raw[BlsParams.LenFpPad..BlsParams.LenFp];
        ReadOnlySpan<byte> fp1 = raw[(BlsParams.LenFp + BlsParams.LenFpPad)..(2 * BlsParams.LenFp)];
        ReadOnlySpan<byte> fp2 = raw[(2 * BlsParams.LenFp + BlsParams.LenFpPad)..(3 * BlsParams.LenFp)];
        ReadOnlySpan<byte> fp3 = raw[(3 * BlsParams.LenFp + BlsParams.LenFpPad)..];

        bool isInfinity = !fp0.ContainsAnyExcept((byte)0)
            && !fp1.ContainsAnyExcept((byte)0)
            && !fp2.ContainsAnyExcept((byte)0)
            && !fp3.ContainsAnyExcept((byte)0);

        if (isInfinity)
        {
            return;
        }

        p.Decode(fp0, fp1, fp2, fp3);
        if (!p.OnCurve())
        {
            throw new BlsPrecompileException("G2 point not on curve.");
        }
    }

    public static byte[] EncodeRaw(this G2 p)
    {
        if (p.IsInf())
        {
            return Enumerable.Repeat<byte>(0, 256).ToArray();
        }

        byte[] raw = new byte[BlsParams.LenG2];
        ReadOnlySpan<byte> trimmed = p.Serialize();
        trimmed[..BlsParams.LenFpTrimmed].CopyTo(raw.AsSpan()[(BlsParams.LenFp + BlsParams.LenFpPad)..]);
        trimmed[BlsParams.LenFpTrimmed..(2 * BlsParams.LenFpTrimmed)].CopyTo(raw.AsSpan()[BlsParams.LenFpPad..BlsParams.LenFp]);
        trimmed[(2 * BlsParams.LenFpTrimmed)..(3 * BlsParams.LenFpTrimmed)].CopyTo(raw.AsSpan()[(3 * BlsParams.LenFp + BlsParams.LenFpPad)..]);
        trimmed[(3 * BlsParams.LenFpTrimmed)..].CopyTo(raw.AsSpan()[(2 * BlsParams.LenFp + BlsParams.LenFpPad)..]);
        return raw;
    }

    public static bool ValidRawFp(ReadOnlySpan<byte> fp)
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
            if (fp[BlsParams.LenFpPad + i] > BlsParams.BaseFieldOrder[i])
            {
                return false;
            }
            else if (fp[BlsParams.LenFpPad + i] < BlsParams.BaseFieldOrder[i])
            {
                return true;
            }
        }
        return false;
    }
}
