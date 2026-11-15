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
    private const int FixedPointCount = 3;

    private const string GeneratorPair =
        "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000002198e9393920d483a7260bfb731fb5d25f1aa493335a9e71297e485b7aef312c21800deef121f1e76426a00665e5c4479674322d4f75edadd46debd5cd992f6ed090689d0585ff075ec9e99ad690c3395bc4b313370b38ef355acdadcd122975b12c85ea5db8c6deb4aab71808dcb408fe3d1e7690c43d37b4ce6cc0166fa7daa";

    private static byte[]? _freshPoints;
    private static byte[]? _fixedPoints;

    private byte[] _input = [];
    private int _freshPerCall;
    private int _next;

    [Params("groth16_fixed_vk", "single_pair_fresh", "four_pairs_fresh")]
    public string Case { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        byte[] generatorPair = Bytes.FromHexString(GeneratorPair);
        if (!BN254PairingCheckPrecompile.Instance.Run(generatorPair, Cancun.Instance))
            throw new InvalidOperationException("Generator pair failed");

        _freshPoints ??= CreatePoints(FreshPoolSize, 0);
        _fixedPoints ??= CreatePoints(FixedPointCount, FreshPoolSize);

        (int pairCount, _freshPerCall) = Case switch
        {
            "groth16_fixed_vk" => (4, 1),
            "single_pair_fresh" => (1, 1),
            "four_pairs_fresh" => (4, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(Case))
        };

        _input = new byte[pairCount * PairSize];
        for (int i = 0; i < pairCount; i++)
        {
            generatorPair.AsSpan(0, G1Size).CopyTo(_input.AsSpan(i * PairSize));
            if (i >= _freshPerCall)
                _fixedPoints.AsSpan((i - _freshPerCall) * G2Size, G2Size).CopyTo(_input.AsSpan(i * PairSize + G1Size));
        }

        _next = 0;
        NextFreshPoints();
        if (!BN254PairingCheckPrecompile.Instance.Run(_input, Cancun.Instance))
            throw new InvalidOperationException("Benchmark input failed");
    }

    [Benchmark]
    public Result<byte[]> CheckPairing()
    {
        NextFreshPoints();
        return BN254PairingCheckPrecompile.Instance.Run(_input, Cancun.Instance);
    }

    private void NextFreshPoints()
    {
        byte[] pool = _freshPoints!;
        for (int i = 0; i < _freshPerCall; i++)
        {
            pool.AsSpan(_next * G2Size, G2Size).CopyTo(_input.AsSpan(i * PairSize + G1Size));
            _next = (_next + 1) % FreshPoolSize;
        }
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

    private static unsafe void WriteFp(byte* target, in mclBnFp value)
    {
        if (Mcl.mclBnFp_getLittleEndian(target, FpSize, value) == 0)
            throw new InvalidOperationException("Field element serialization failed");

        new Span<byte>(target, FpSize).Reverse();
    }
}
