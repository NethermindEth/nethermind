// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark;

/// <summary>
/// Times <see cref="KzgPointEvaluationPrecompile"/> on valid openings generated against the mainnet trusted setup.
/// </summary>
/// <remarks>
/// The openings are generated rather than read from <c>bench_precompiles</c>, whose success vector does not verify
/// against the mainnet trusted setup and so timed the failure path. Each one is checked to verify before timing.
/// </remarks>
public class KzgPointEvaluationBenchmark
{
    private const int InputCount = 8;
    private const int InputLength = 192;

    private byte[][] _inputs = [];

    // Only the gas columns read Input; the timed inputs are generated in GlobalSetup.
    public static IEnumerable<PrecompileBenchmarkBase.Param> Inputs()
    {
        yield return new(KzgPointEvaluationPrecompile.Instance, "valid_openings", new byte[InputLength], null);
    }

    [ParamsSource(nameof(Inputs))]
    public PrecompileBenchmarkBase.Param Input { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        await KzgPolynomialCommitments.InitializeAsync();

        nint setup = Ckzg.LoadTrustedSetup(Path.Combine(AppContext.BaseDirectory, "kzg_trusted_setup.txt"), 0);
        try
        {
            Random random = new(1);
            _inputs = new byte[InputCount][];
            for (int i = 0; i < InputCount; i++)
            {
                _inputs[i] = GenerateOpening(random, setup);
                Result<byte[]> result = KzgPointEvaluationPrecompile.Instance.Run(_inputs[i], Cancun.Instance);
                if (!result)
                    throw new InvalidOperationException($"{nameof(KzgPointEvaluationBenchmark)} input {i} does not verify: {result.Error}");
            }
        }
        finally
        {
            Ckzg.FreeTrustedSetup(setup);
        }
    }

    private static byte[] GenerateOpening(Random random, nint setup)
    {
        byte[] blob = new byte[Ckzg.BytesPerBlob];
        random.NextBytes(blob);
        // a zero top byte keeps every field element below the BLS modulus
        for (int j = 0; j < blob.Length; j += Ckzg.BytesPerFieldElement)
            blob[j] = 0;

        byte[] z = new byte[Ckzg.BytesPerFieldElement];
        random.NextBytes(z);
        z[0] = 0;

        byte[] y = new byte[Ckzg.BytesPerFieldElement];
        byte[] commitment = new byte[Ckzg.BytesPerCommitment];
        byte[] proof = new byte[Ckzg.BytesPerProof];
        byte[] versionedHash = new byte[32];
        Ckzg.BlobToKzgCommitment(commitment, blob, setup);
        Ckzg.ComputeKzgProof(proof, y, blob, z, setup);
        KzgPolynomialCommitments.TryComputeCommitmentHashV1(commitment, versionedHash);

        return [.. versionedHash, .. z, .. y, .. commitment, .. proof];
    }

    [Benchmark(OperationsPerInvoke = InputCount)]
    public bool PointEvaluation()
    {
        bool overallResult = true;
        foreach (byte[] input in _inputs)
            overallResult &= KzgPointEvaluationPrecompile.Instance.Run(input, Cancun.Instance);
        return overallResult;
    }
}
