// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
internal static class Eip2537
{
    internal const bool DisableConcurrency = false;
    internal const bool DisableSubgroupChecks = false;
    internal const int LenFr = 32;
    internal const int LenFp = 64;
    internal const int LenFpTrimmed = 48;
    internal const int LenFpPad = LenFp - LenFpTrimmed;
    internal const int LenG1 = 2 * LenFp;
    internal const int LenG1Trimmed = 2 * LenFpTrimmed;
    internal const int LenG2 = 4 * LenFp;
    internal const int LenG2Trimmed = 4 * LenFpTrimmed;

    internal static readonly byte[] G1Infinity = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    internal static readonly byte[] G2Infinity = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private const int _maxDiscountG1 = 519;
    private const int _maxDiscountG2 = 524;

    private static readonly byte[] _baseFieldOrder = [0x1a, 0x01, 0x11, 0xea, 0x39, 0x7f, 0xe6, 0x9a, 0x4b, 0x1b, 0xa7, 0xb6, 0x43, 0x4b, 0xac, 0xd7, 0x64, 0x77, 0x4b, 0x84, 0xf3, 0x85, 0x12, 0xbf, 0x67, 0x30, 0xd2, 0xa0, 0xf6, 0xb0, 0xf6, 0x24, 0x1e, 0xab, 0xff, 0xfe, 0xb1, 0x53, 0xff, 0xff, 0xb9, 0xfe, 0xff, 0xff, 0xff, 0xff, 0xaa, 0xab];
    private static readonly (int g1, int g2)[] _discountTable =
    [
        (0, 0), // 0
        (1000, 1000), // 1
        (949, 1000), // 2
        (848, 923), // 3
        (797, 884), // 4
        (764, 855), // 5
        (750, 832), // 6
        (738, 812), // 7
        (728, 796), // 8
        (719, 782), // 9
        (712, 770), // 10
        (705, 759), // 11
        (698, 749), // 12
        (692, 740), // 13
        (687, 732), // 14
        (682, 724), // 15
        (677, 717), // 16
        (673, 711), // 17
        (669, 704), // 18
        (665, 699), // 19
        (661, 693), // 20
        (658, 688), // 21
        (654, 683), // 22
        (651, 679), // 23
        (648, 674), // 24
        (645, 670), // 25
        (642, 666), // 26
        (640, 663), // 27
        (637, 659), // 28
        (635, 655), // 29
        (632, 652), // 30
        (630, 649), // 31
        (627, 646), // 32
        (625, 643), // 33
        (623, 640), // 34
        (621, 637), // 35
        (619, 634), // 36
        (617, 632), // 37
        (615, 629), // 38
        (613, 627), // 39
        (611, 624), // 40
        (609, 622), // 41
        (608, 620), // 42
        (606, 618), // 43
        (604, 615), // 44
        (603, 613), // 45
        (601, 611), // 46
        (599, 609), // 47
        (598, 607), // 48
        (596, 606), // 49
        (595, 604), // 50
        (593, 602), // 51
        (592, 600), // 52
        (591, 598), // 53
        (589, 597), // 54
        (588, 595), // 55
        (586, 593), // 56
        (585, 592), // 57
        (584, 590), // 58
        (582, 589), // 59
        (581, 587), // 60
        (580, 586), // 61
        (579, 584), // 62
        (577, 583), // 63
        (576, 582), // 64
        (575, 580), // 65
        (574, 579), // 66
        (573, 578), // 67
        (572, 576), // 68
        (570, 575), // 69
        (569, 574), // 70
        (568, 573), // 71
        (567, 571), // 72
        (566, 570), // 73
        (565, 569), // 74
        (564, 568), // 75
        (563, 567), // 76
        (562, 566), // 77
        (561, 565), // 78
        (560, 563), // 79
        (559, 562), // 80
        (558, 561), // 81
        (557, 560), // 82
        (556, 559), // 83
        (555, 558), // 84
        (554, 557), // 85
        (553, 556), // 86
        (552, 555), // 87
        (551, 554), // 88
        (550, 553), // 89
        (549, 552), // 90
        (548, 552), // 91
        (547, 551), // 92
        (547, 550), // 93
        (546, 549), // 94
        (545, 548), // 95
        (544, 547), // 96
        (543, 546), // 97
        (542, 545), // 98
        (541, 545), // 99
        (540, 544), // 100
        (540, 543), // 101
        (539, 542), // 102
        (538, 541), // 103
        (537, 541), // 104
        (536, 540), // 105
        (536, 539), // 106
        (535, 538), // 107
        (534, 537), // 108
        (533, 537), // 109
        (532, 536), // 110
        (532, 535), // 111
        (531, 535), // 112
        (530, 534), // 113
        (529, 533), // 114
        (528, 532), // 115
        (528, 532), // 116
        (527, 531), // 117
        (526, 530), // 118
        (525, 530), // 119
        (525, 529), // 120
        (524, 528), // 121
        (523, 528), // 122
        (522, 527), // 123
        (522, 526), // 124
        (521, 526), // 125
        (520, 525), // 126
        (520, 524), // 127
        (519, 524) // 128
    ];

    internal static int DiscountForG1(int k) => k >= 128 ? _maxDiscountG1 : _discountTable[k].g1;

    internal static int DiscountForG2(int k) => k >= 128 ? _maxDiscountG2 : _discountTable[k].g2;

    internal static Result TryDecodeRaw(this G1 p, ReadOnlySpan<byte> raw)
    {
        if (raw.Length != LenG1)
            return Errors.InvalidFieldLength;

        Result result = ValidRawFp(raw[..LenFp]) && ValidRawFp(raw[LenFp..]);

        if (result)
        {
            // set to infinity point by default
            p.Zero();

            ReadOnlySpan<byte> fp0 = raw[LenFpPad..LenFp];
            ReadOnlySpan<byte> fp1 = raw[(LenFp + LenFpPad)..];

            bool isInfinity = !fp0.ContainsAnyExcept((byte)0) && !fp1.ContainsAnyExcept((byte)0);

            if (isInfinity)
                return Result.Success;

            p.Decode(fp0, fp1);

            return p.OnCurve() ? Result.Success : Errors.G1PointSubgroup;
        }

        return result;
    }


    // decodes and checks point is on curve
    internal static Result TryDecodeRaw(this G2 p, ReadOnlySpan<byte> raw)
    {
        if (raw.Length != LenG2)
            return Errors.InvalidFieldLength;

        Result result =
            ValidRawFp(raw[..LenFp]) &&
            ValidRawFp(raw[LenFp..(2 * LenFp)]) &&
            ValidRawFp(raw[(2 * LenFp)..(3 * LenFp)]) &&
            ValidRawFp(raw[(3 * LenFp)..]);

        if (result)
        {
            // set to infinity point by default
            p.Zero();

            ReadOnlySpan<byte> fp0 = raw[LenFpPad..LenFp];
            ReadOnlySpan<byte> fp1 = raw[(LenFp + LenFpPad)..(2 * LenFp)];
            ReadOnlySpan<byte> fp2 = raw[(2 * LenFp + LenFpPad)..(3 * LenFp)];
            ReadOnlySpan<byte> fp3 = raw[(3 * LenFp + LenFpPad)..];

            bool isInfinity =
                !fp0.ContainsAnyExcept((byte)0) &&
                !fp1.ContainsAnyExcept((byte)0) &&
                !fp2.ContainsAnyExcept((byte)0) &&
                !fp3.ContainsAnyExcept((byte)0);

            if (isInfinity)
                return Result.Success;

            p.Decode(fp0, fp1, fp2, fp3);

            return p.OnCurve() ? Result.Success : Errors.G2PointSubgroup;
        }

        return result;
    }

    internal static byte[] EncodeRaw(this G1 p)
    {
        if (p.IsInf())
            return G1Infinity;

        // copy serialized point to buffer with padding bytes
        byte[] raw = new byte[LenG1];
        ReadOnlySpan<byte> trimmed = p.Serialize();

        trimmed[..LenFpTrimmed].CopyTo(raw.AsSpan()[LenFpPad..LenFp]);
        trimmed[LenFpTrimmed..].CopyTo(raw.AsSpan()[(LenFp + LenFpPad)..]);

        return raw;
    }

    internal static byte[] EncodeRaw(this G2 p)
    {
        if (p.IsInf())
            return G2Infinity;

        // copy serialized point to buffer with padding bytes
        byte[] raw = new byte[LenG2];
        ReadOnlySpan<byte> trimmed = p.Serialize();

        trimmed[..LenFpTrimmed].CopyTo(raw.AsSpan()[(LenFp + LenFpPad)..]);
        trimmed[LenFpTrimmed..(2 * LenFpTrimmed)].CopyTo(raw.AsSpan()[LenFpPad..LenFp]);
        trimmed[(2 * LenFpTrimmed)..(3 * LenFpTrimmed)].CopyTo(raw.AsSpan()[(3 * LenFp + LenFpPad)..]);
        trimmed[(3 * LenFpTrimmed)..].CopyTo(raw.AsSpan()[(2 * LenFp + LenFpPad)..]);

        return raw;
    }

    internal static Result ValidRawFp(ReadOnlySpan<byte> fp)
    {
        if (fp.Length != LenFp)
            return Errors.InvalidFieldLength;

        // check that padding bytes are zeroes
        if (fp[..LenFpPad].ContainsAnyExcept((byte)0))
            return Errors.InvalidFieldElementTopBytes;

        // check that fp < base field order
        return fp[LenFpPad..].SequenceCompareTo(_baseFieldOrder.AsSpan()) < 0
            ? Result.Success
            : Errors.G1PointSubgroup;
    }

    internal static Result TryDecodeG1ToBuffer(
        ReadOnlyMemory<byte> inputData,
        Memory<long> pointBuffer,
        Memory<byte> scalarBuffer,
        int dest,
        int index
        ) => TryDecodePointToBuffer(
            inputData,
            pointBuffer,
            scalarBuffer,
            dest,
            index,
            LenG1,
            Bls12381G1MsmPrecompile.ItemSize,
            DecodeAndCheckSubgroupG1);

    internal static Result TryDecodeG2ToBuffer(
        ReadOnlyMemory<byte> inputData,
        Memory<long> pointBuffer,
        Memory<byte> scalarBuffer,
        int dest,
        int index
        ) => TryDecodePointToBuffer(
            inputData,
            pointBuffer,
            scalarBuffer,
            dest,
            index,
            LenG2,
            Bls12381G2MsmPrecompile.ItemSize,
            DecodeAndCheckSubgroupG2);

    private static Result DecodeAndCheckSubgroupG1(ReadOnlyMemory<byte> rawPoint, Memory<long> pointBuffer, int dest)
    {
        G1 p = new(pointBuffer.Span[(dest * G1.Sz)..]);
        Result result = p.TryDecodeRaw(rawPoint.Span);

        return result
            ? DisableSubgroupChecks || p.InGroup() ? Result.Success : Errors.G1PointSubgroup
            : result;
    }

    private static Result DecodeAndCheckSubgroupG2(ReadOnlyMemory<byte> rawPoint, Memory<long> pointBuffer, int dest)
    {
        G2 p = new(pointBuffer.Span[(dest * G2.Sz)..]);
        Result result = p.TryDecodeRaw(rawPoint.Span);

        return result
            ? DisableSubgroupChecks || p.InGroup() ? Result.Success : Errors.G2PointSubgroup
            : result;
    }

    private static Result TryDecodePointToBuffer(
        ReadOnlyMemory<byte> inputData,
        Memory<long> pointBuffer,
        Memory<byte> scalarBuffer,
        int dest,
        int index,
        int pointLen,
        int itemSize,
        Func<ReadOnlyMemory<byte>,
        Memory<long>, int, Result> decodeAndCheckPoint)
    {
        // skip points at infinity
        if (dest == -1)
            return Result.Success;

        int offset = index * itemSize;
        ReadOnlyMemory<byte> rawPoint = inputData[offset..(offset + pointLen)];
        ReadOnlyMemory<byte> reversedScalar = inputData[(offset + pointLen)..(offset + itemSize)];

        Result result = decodeAndCheckPoint(rawPoint, pointBuffer, dest);

        if (!result)
            return result;

        int destOffset = dest * 32;

        reversedScalar.CopyTo(scalarBuffer[destOffset..]);
        scalarBuffer[destOffset..(destOffset + 32)].Span.Reverse();

        return Result.Success;
    }
}
