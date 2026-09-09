// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark;

/// <summary>
/// Microbenchmark targeting the distinct branches of <see cref="BN254"/>.<c>CheckPairing</c>: the single-pair
/// fast path, the vectorized multi-pair path, the multi-chunk accumulation past <c>MaxStackPairCount</c>, and the
/// point-at-infinity skip (a zero pair is detected during deserialization and excluded from the Miller loop without
/// re-scanning the deserialized struct).
/// </summary>
/// <remarks>
/// Complements the fixture-driven <see cref="BN254PairingBenchmark"/>, which feeds whatever EF vectors are on disk and
/// only the first line of each. Here the inputs are fixed and each pairing product is known analytically, so the
/// numbers are reproducible and every named case maps to exactly one branch of <c>CheckPairing</c>. Reuses the
/// gas-throughput columns by exposing the input as a <see cref="PrecompileBenchmarkBase.Param"/>.
/// </remarks>
public class BN254PairingCheckPathsBenchmark
{
    // EIP-197 vector with e(P, Q) * e(-P, Q) == 1, so repeating the block leaves the product equal to one.
    private const string TwoPairsProductOne =
        "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000002198e9393920d483a7260bfb731fb5d25f1aa493335a9e71297e485b7aef312c21800deef121f1e76426a00665e5c4479674322d4f75edadd46debd5cd992f6ed090689d0585ff075ec9e99ad690c3395bc4b313370b38ef355acdadcd122975b12c85ea5db8c6deb4aab71808dcb408fe3d1e7690c43d37b4ce6cc0166fa7daa00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000002198e9393920d483a7260bfb731fb5d25f1aa493335a9e71297e485b7aef312c21800deef121f1e76426a00665e5c4479674322d4f75edadd46debd5cd992f6ed275dc4a288d1afb3cbb1ac09187524c7db36395df7be3b99e673b13a075a65ec1d9befcd05a5323e6da4d435f3b617cdb3af83285c2df711ef39c01571827f9d";

    // A single valid pair whose pairing is not one (the call succeeds, result byte 0).
    private const string OnePairNotOne =
        "0341b65d1b32805aedf29c4704ae125b98bb9b736d6e05bd934320632bf46bb60d22bc985718acbcf51e3740c1565f66ff890dfd2302fc51abc999c83d8774ba0d2c492bf135ed45b0d6265c274d145d35b73afd41ee95d3f1da4bc8761038800251d138db1b9748ffc257b147a1aea66413b14df767f98f7ba02489c617eae51065ff2bd9a5b167db36225a35fd712d781309f4e2c8541a335b2c42bd2bcae4191cd528d749c52f3e198e534868d537867109419a32314886f6bb2bcd337773";

    private const int PairHexLength = 384; // a 192-byte BN254 pair encoded as hex

    private static string Repeat(string pair, int times)
    {
        StringBuilder builder = new(pair.Length * times);
        for (int i = 0; i < times; i++)
            builder.Append(pair);
        return builder.ToString();
    }

    private static string InfinityPairs(int count) => new('0', PairHexLength * count);

    public static IEnumerable<PrecompileBenchmarkBase.Param> Inputs()
    {
        IPrecompile precompile = BN254PairingCheckPrecompile.Instance;

        // 1 pair -> single-pairing fast path.
        yield return Param("single_pair", OnePairNotOne);
        // 2 pairs -> vectorized multi-Miller-loop, a single chunk.
        yield return Param("two_pairs_vectorized", TwoPairsProductOne);
        // Leading point at infinity then 2 valid pairs -> the zero pair is skipped, product unchanged.
        yield return Param("leading_infinity_skip", InfinityPairs(1) + TwoPairsProductOne);
        // 34 pairs -> two chunks (32 + 2): multi-chunk GT accumulation past MaxStackPairCount.
        yield return Param("34_pairs_multi_chunk", Repeat(TwoPairsProductOne, 17));

        PrecompileBenchmarkBase.Param Param(string name, string hex) =>
            new(precompile, name, Bytes.FromHexString(hex), null);
    }

    [ParamsSource(nameof(Inputs))]
    public PrecompileBenchmarkBase.Param Input { get; set; }

    [Benchmark]
    public Result<byte[]> CheckPairing() => Input.Precompile.Run(Input.Bytes, Cancun.Instance);
}
