// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Blockchain.Precompiles.Bls;

public static class BlsExtensions
{
    // decodes and checks point is on curve
    public static bool TryDecodeRaw(this G1 p, ReadOnlySpan<byte> raw)
    {
        if (raw.Length != BlsConst.LenG1)
        {
            return false;
        }

        if (!ValidRawFp(raw[..BlsConst.LenFp]) || !ValidRawFp(raw[BlsConst.LenFp..]))
        {
            return false;
        }

        // set to infinity point by default
        p.Zero();

        ReadOnlySpan<byte> fp0 = raw[BlsConst.LenFpPad..BlsConst.LenFp];
        ReadOnlySpan<byte> fp1 = raw[(BlsConst.LenFp + BlsConst.LenFpPad)..];

        bool isInfinity = !fp0.ContainsAnyExcept((byte)0) && !fp1.ContainsAnyExcept((byte)0);
        if (isInfinity)
        {
            return true;
        }

        p.Decode(fp0, fp1);
        if (!p.OnCurve())
        {
            return false;
        }

        return true;
    }

    // decodes and checks point is on curve
    public static bool TryDecodeRaw(this G2 p, ReadOnlySpan<byte> raw)
    {
        if (raw.Length != BlsConst.LenG2)
        {
            return false;
        }

        if (!ValidRawFp(raw[..BlsConst.LenFp]) ||
            !ValidRawFp(raw[BlsConst.LenFp..(2 * BlsConst.LenFp)]) ||
            !ValidRawFp(raw[(2 * BlsConst.LenFp)..(3 * BlsConst.LenFp)]) ||
            !ValidRawFp(raw[(3 * BlsConst.LenFp)..]))
        {
            return false;
        }

        // set to infinity point by default
        p.Zero();

        ReadOnlySpan<byte> fp0 = raw[BlsConst.LenFpPad..BlsConst.LenFp];
        ReadOnlySpan<byte> fp1 = raw[(BlsConst.LenFp + BlsConst.LenFpPad)..(2 * BlsConst.LenFp)];
        ReadOnlySpan<byte> fp2 = raw[(2 * BlsConst.LenFp + BlsConst.LenFpPad)..(3 * BlsConst.LenFp)];
        ReadOnlySpan<byte> fp3 = raw[(3 * BlsConst.LenFp + BlsConst.LenFpPad)..];

        bool isInfinity = !fp0.ContainsAnyExcept((byte)0)
            && !fp1.ContainsAnyExcept((byte)0)
            && !fp2.ContainsAnyExcept((byte)0)
            && !fp3.ContainsAnyExcept((byte)0);

        if (isInfinity)
        {
            return true;
        }

        p.Decode(fp0, fp1, fp2, fp3);
        if (!p.OnCurve())
        {
            return false;
        }

        return true;
    }

    public static byte[] EncodeRaw(this G1 p)
    {
        if (p.IsInf())
        {
            return BlsConst.G1Inf;
        }

        // copy serialized point to buffer with padding bytes
        byte[] raw = new byte[BlsConst.LenG1];
        ReadOnlySpan<byte> trimmed = p.Serialize();
        trimmed[..BlsConst.LenFpTrimmed].CopyTo(raw.AsSpan()[BlsConst.LenFpPad..BlsConst.LenFp]);
        trimmed[BlsConst.LenFpTrimmed..].CopyTo(raw.AsSpan()[(BlsConst.LenFp + BlsConst.LenFpPad)..]);
        return raw;
    }

    public static byte[] EncodeRaw(this G2 p)
    {
        if (p.IsInf())
        {
            return BlsConst.G2Inf;
        }

        // copy serialized point to buffer with padding bytes
        byte[] raw = new byte[BlsConst.LenG2];
        ReadOnlySpan<byte> trimmed = p.Serialize();
        trimmed[..BlsConst.LenFpTrimmed].CopyTo(raw.AsSpan()[(BlsConst.LenFp + BlsConst.LenFpPad)..]);
        trimmed[BlsConst.LenFpTrimmed..(2 * BlsConst.LenFpTrimmed)].CopyTo(raw.AsSpan()[BlsConst.LenFpPad..BlsConst.LenFp]);
        trimmed[(2 * BlsConst.LenFpTrimmed)..(3 * BlsConst.LenFpTrimmed)].CopyTo(raw.AsSpan()[(3 * BlsConst.LenFp + BlsConst.LenFpPad)..]);
        trimmed[(3 * BlsConst.LenFpTrimmed)..].CopyTo(raw.AsSpan()[(2 * BlsConst.LenFp + BlsConst.LenFpPad)..]);
        return raw;
    }

    public static bool ValidRawFp(ReadOnlySpan<byte> fp)
    {
        if (fp.Length != BlsConst.LenFp)
        {
            return false;
        }

        // check that padding bytes are zeroes
        if (fp[..BlsConst.LenFpPad].ContainsAnyExcept((byte)0))
        {
            return false;
        }

        // check that fp < base field order
        return fp[BlsConst.LenFpPad..].SequenceCompareTo(BlsConst.BaseFieldOrder.AsSpan()) < 0;
    }

    public static bool TryDecodeG1ToBuffer(ReadOnlyMemory<byte> inputData, Memory<long> pointBuffer, Memory<byte> scalarBuffer, int dest, int index)
        => TryDecodePointToBuffer(inputData, pointBuffer, scalarBuffer, dest, index, BlsConst.LenG1, G1MSMPrecompile.ItemSize, DecodeAndCheckSubgroupG1);

    public static bool TryDecodeG2ToBuffer(ReadOnlyMemory<byte> inputData, Memory<long> pointBuffer, Memory<byte> scalarBuffer, int dest, int index)
        => TryDecodePointToBuffer(inputData, pointBuffer, scalarBuffer, dest, index, BlsConst.LenG2, G2MSMPrecompile.ItemSize, DecodeAndCheckSubgroupG2);

    private static bool DecodeAndCheckSubgroupG1(ReadOnlyMemory<byte> rawPoint, Memory<long> pointBuffer, int dest)
    {
        G1 p = new(pointBuffer.Span[(dest * G1.Sz)..]);
        return p.TryDecodeRaw(rawPoint.Span) && (BlsConst.DisableSubgroupChecks || p.InGroup());
    }

    private static bool DecodeAndCheckSubgroupG2(ReadOnlyMemory<byte> rawPoint, Memory<long> pointBuffer, int dest)
    {
        G2 p = new(pointBuffer.Span[(dest * G2.Sz)..]);
        return p.TryDecodeRaw(rawPoint.Span) && (BlsConst.DisableSubgroupChecks || p.InGroup());
    }

    private static bool TryDecodePointToBuffer(
        ReadOnlyMemory<byte> inputData,
        Memory<long> pointBuffer,
        Memory<byte> scalarBuffer,
        int dest,
        int index,
        int pointLen,
        int itemSize,
        Func<ReadOnlyMemory<byte>, Memory<long>, int, bool> decodeAndCheckPoint)
    {
        // skip points at infinity
        if (dest == -1)
        {
            return true;
        }

        int offset = index * itemSize;
        ReadOnlyMemory<byte> rawPoint = inputData[offset..(offset + pointLen)];
        ReadOnlyMemory<byte> reversedScalar = inputData[(offset + pointLen)..(offset + itemSize)];

        if (!decodeAndCheckPoint(rawPoint, pointBuffer, dest))
        {
            return false;
        }

        int destOffset = dest * 32;
        reversedScalar.CopyTo(scalarBuffer[destOffset..]);
        scalarBuffer[destOffset..(destOffset + 32)].Span.Reverse();
        return true;
    }
}
