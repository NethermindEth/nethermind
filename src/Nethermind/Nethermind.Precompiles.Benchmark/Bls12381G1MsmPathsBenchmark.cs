// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark;

/// <summary>
/// Microbenchmark targeting the distinct internal paths of the BLS12-381 G1MSM precompile (<c>0x0c</c>):
/// the single-point <c>Mul</c> shortcut, the multi-point <c>Msm</c> path while its decode buffer still fits the
/// stackalloc threshold, and the same path once it spills to a pooled buffer past that threshold.
/// </summary>
/// <remarks>
/// Complements the fixture-driven <see cref="Bls12381G1MsmBenchmark"/>, which feeds whatever EF vectors are on disk
/// and only the first line of each, by pinning the vector size to each branch boundary so every named case maps to
/// exactly one code path. <c>Run</c> dispatches to <c>Mul</c> at one pair and to <c>Msm</c> otherwise; inside <c>Msm</c>
/// the decoded layout is stackalloc'd up to <c>StackallocPairCountThreshold</c> (8) pairs and rented from the
/// <see cref="System.Buffers.ArrayPool{T}"/> beyond it. With <see cref="MemoryDiagnoserAttribute"/> the boundary cases
/// (8 vs 9) surface the managed-allocation crossover, and the gas-throughput columns are reused by exposing the input
/// as a <see cref="PrecompileBenchmarkBase.Param"/>. The input repeats a single valid in-subgroup G1 point with a
/// non-zero scalar, so every vector exercises the full decode + native MSM path with no point-at-infinity short-circuit.
/// </remarks>
[MemoryDiagnoser]
public class Bls12381G1MsmPathsBenchmark
{
    // One valid 160-byte G1MSM item: a 128-byte G1 point in the correct subgroup followed by a 32-byte scalar.
    private const string ValidItem =
        "0000000000000000000000000000000012196c5a43d69224d8713389285f26b98f86ee910ab3dd668e413738282003cc5b7357af9a7af54bb713d62255e80f560000000000000000000000000000000006ba8102bfbeea4416b710c73e8cce3032c31c6269c44906f8ac4f7874ce99fb17559992486528963884ce429a992feeb3c940fe79b6966489b527955de7599194a9ac69a6ff58b8d99e7b1084f0464e";

    private static string Repeat(int pairs) => string.Concat(System.Linq.Enumerable.Repeat(ValidItem, pairs));

    public static IEnumerable<PrecompileBenchmarkBase.Param> Inputs()
    {
        IPrecompile precompile = Bls12381G1MsmPrecompile.Instance;

        // 1 pair -> Run dispatches to the Mul shortcut, bypassing the Msm decode loop entirely.
        yield return Param("mul_single", 1);
        // 2 pairs -> Msm with the decoded layout on the stack.
        yield return Param("msm_stackalloc_2", 2);
        // 8 pairs -> last vector size still served by stackalloc (StackallocPairCountThreshold).
        yield return Param("msm_stackalloc_8_boundary", 8);
        // 9 pairs -> first vector size that spills the decoded layout to the ArrayPool.
        yield return Param("msm_arraypool_9_boundary", 9);
        // 16 pairs -> pooled-buffer path with the native MSM cost amortized over more points.
        yield return Param("msm_arraypool_16", 16);

        PrecompileBenchmarkBase.Param Param(string name, int pairs) =>
            new(precompile, name, Bytes.FromHexString(Repeat(pairs)), null);
    }

    [ParamsSource(nameof(Inputs))]
    public PrecompileBenchmarkBase.Param Input { get; set; }

    [Benchmark]
    public Result<byte[]> Msm() => Input.Precompile.Run(Input.Bytes, Prague.Instance);
}
