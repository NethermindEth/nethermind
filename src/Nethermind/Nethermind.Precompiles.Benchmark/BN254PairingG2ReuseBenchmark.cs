// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.MclBindings;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark;

public class BN254PairingG2ReuseBenchmark
{
    private const int G1Size = 64;
    private const int G2Size = 128;
    private const int FpSize = 32;
    private const int PairSize = G1Size + G2Size;
    private const int FreshPoolSize = 16384;
    private const int FixedPointCount = 4;
    private const int RotationSize = 2048;
    private const uint PairCacheSlotMask = 1023;
    private const uint PairSeenSlotMask = 4095;

    private const string GeneratorPair =
        "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000002198e9393920d483a7260bfb731fb5d25f1aa493335a9e71297e485b7aef312c21800deef121f1e76426a00665e5c4479674322d4f75edadd46debd5cd992f6ed090689d0585ff075ec9e99ad690c3395bc4b313370b38ef355acdadcd122975b12c85ea5db8c6deb4aab71808dcb408fe3d1e7690c43d37b4ce6cc0166fa7daa";

    private static byte[]? _freshPoints;
    private static byte[]? _fixedPoints;
    private static byte[]? _freshG1Points;

    private byte[] _input = [];
    private byte[][] _alternating = [];
    private int _freshPerCall;
    private int _freshCycle;
    private int _pairsPerFreshPoint;
    private int _calls;
    private int _freshG1PerCall;
    private int _next;
    private int _nextG1;

    [Params("groth16_fixed_vk", "groth16_fixed_alpha_beta", "four_fixed_pairs", "single_pair_fresh", "four_pairs_fresh", "rotating_repeated_pairs", "duplicate_new_pairs", "slot_collision")]
    public string Case { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        byte[] generatorPair = Bytes.FromHexString(GeneratorPair);
        if (!BN254PairingCheckPrecompile.Instance.Run(generatorPair, Cancun.Instance))
            throw new InvalidOperationException("Generator pair failed");

        _freshPoints ??= CreatePoints(FreshPoolSize, 0);
        _fixedPoints ??= CreatePoints(FixedPointCount, FreshPoolSize);
        _freshG1Points ??= CreateG1Points(FreshPoolSize);

        (int pairCount, _freshPerCall, _freshG1PerCall) = Case switch
        {
            "groth16_fixed_vk" => (4, 1, 0),
            "groth16_fixed_alpha_beta" => (4, 1, 3),
            "four_fixed_pairs" => (4, 0, 0),
            "single_pair_fresh" => (1, 1, 0),
            "four_pairs_fresh" or "rotating_repeated_pairs" or "duplicate_new_pairs" => (4, 4, 0),
            "slot_collision" => (4, 0, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(Case))
        };
        _freshCycle = Case == "rotating_repeated_pairs" ? RotationSize : FreshPoolSize;
        _pairsPerFreshPoint = Case == "duplicate_new_pairs" ? 2 : 1;

        _input = new byte[pairCount * PairSize];
        for (int i = 0; i < pairCount; i++)
        {
            generatorPair.AsSpan(0, G1Size).CopyTo(_input.AsSpan(i * PairSize));
            if (i >= _freshPerCall)
                _fixedPoints.AsSpan((i - _freshPerCall) * G2Size, G2Size).CopyTo(_input.AsSpan(i * PairSize + G1Size));
        }

        _alternating = Case == "slot_collision" ? CreateCollidingInputs(generatorPair, pairCount) : [_input, _input];

        _next = 0;
        _nextG1 = 0;
        _calls = 0;
        NextFreshPoints();
        if (!BN254PairingCheckPrecompile.Instance.Run(_alternating[1], Cancun.Instance))
            throw new InvalidOperationException("Benchmark input failed");
    }

    [Benchmark]
    public Result<byte[]> CheckPairing()
    {
        NextFreshPoints();
        return BN254PairingCheckPrecompile.Instance.Run(_alternating[_calls++ & 1], Cancun.Instance);
    }

    private void NextFreshPoints()
    {
        byte[] pool = _freshPoints!;
        for (int i = 0; i < _freshPerCall; i++)
        {
            pool.AsSpan(_next * G2Size, G2Size).CopyTo(_input.AsSpan(i * PairSize + G1Size));
            if ((i + 1) % _pairsPerFreshPoint == 0)
                _next = (_next + 1) % _freshCycle;
        }

        byte[] g1Pool = _freshG1Points!;
        for (int i = 0; i < _freshG1PerCall; i++)
        {
            int pair = i == 0 ? 0 : i + 1;
            g1Pool.AsSpan(_nextG1 * G1Size, G1Size).CopyTo(_input.AsSpan(pair * PairSize));
            _nextG1 = (_nextG1 + 1) % FreshPoolSize;
        }
    }

    private static byte[][] CreateCollidingInputs(byte[] generatorPair, int pairCount)
    {
        byte[] pool = _freshPoints!;
        byte[] pair = new byte[PairSize];
        generatorPair.AsSpan(0, G1Size).CopyTo(pair);
        int[] firstPoint = new int[PairCacheSlotMask + 1];
        uint[] firstSeen = new uint[PairCacheSlotMask + 1];
        firstPoint.AsSpan().Fill(-1);

        byte[][] inputs = [new byte[pairCount * PairSize], new byte[pairCount * PairSize]];
        int found = 0;
        for (int point = 0; point < FreshPoolSize && found < pairCount; point++)
        {
            pool.AsSpan(point * G2Size, G2Size).CopyTo(pair.AsSpan(G1Size));
            uint hash = (uint)new ReadOnlySpan<byte>(pair).FastHash();
            uint slot = hash & PairCacheSlotMask;
            uint seen = hash & PairSeenSlotMask;
            if (firstPoint[slot] == -1)
            {
                firstPoint[slot] = point;
                firstSeen[slot] = seen;
            }
            else if (firstPoint[slot] >= 0 && firstSeen[slot] != seen)
            {
                generatorPair.AsSpan(0, G1Size).CopyTo(inputs[0].AsSpan(found * PairSize));
                pool.AsSpan(firstPoint[slot] * G2Size, G2Size).CopyTo(inputs[0].AsSpan(found * PairSize + G1Size));
                pair.CopyTo(inputs[1].AsSpan(found * PairSize));
                firstPoint[slot] = -2;
                found++;
            }
        }

        if (found < pairCount)
            throw new InvalidOperationException("Not enough colliding pairs");

        return inputs;
    }

    private static unsafe byte[] CreatePoints(int count, int seed)
    {
        byte[] points = new byte[count * G2Size];
        fixed (byte* output = points)
        {
            for (int i = 0; i < count; i++)
            {
                int message = seed + i;
                mclBnG2 point = default;
                if (Mcl.mclBnG2_hashAndMapTo(ref point, &message, sizeof(int)) != 0)
                    throw new InvalidOperationException("Hash to G2 failed");

                Mcl.mclBnG2_normalize(ref point, point);
                byte* target = output + i * G2Size;
                WriteFp(target, point.x.d1);
                WriteFp(target + FpSize, point.x.d0);
                WriteFp(target + 2 * FpSize, point.y.d1);
                WriteFp(target + 3 * FpSize, point.y.d0);
            }
        }

        return points;
    }

    private static unsafe byte[] CreateG1Points(int count)
    {
        byte[] points = new byte[count * G1Size];
        fixed (byte* output = points)
        {
            for (int i = 0; i < count; i++)
            {
                int message = i;
                mclBnG1 point = default;
                if (Mcl.mclBnG1_hashAndMapTo(ref point, &message, sizeof(int)) != 0)
                    throw new InvalidOperationException("Hash to G1 failed");

                Mcl.mclBnG1_normalize(ref point, point);
                byte* target = output + i * G1Size;
                WriteFp(target, point.x);
                WriteFp(target + FpSize, point.y);
            }
        }

        return points;
    }

    private static unsafe void WriteFp(byte* target, in mclBnFp value)
    {
        if (Mcl.mclBnFp_getLittleEndian(target, FpSize, value) == 0)
            throw new InvalidOperationException("Field element serialization failed");

        new Span<byte>(target, FpSize).Reverse();
    }
}
