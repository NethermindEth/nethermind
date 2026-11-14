// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark;

/// <summary>
/// Times <see cref="ECRecoverPrecompile"/> on distinct valid signatures, and separately on a repeated one.
/// </summary>
/// <remarks>
/// The precompile keeps its last input and result per thread, so repeating one input times a cache hit rather than a
/// public key recovery. <see cref="Recover"/> cycles through distinct signatures so that every call misses the cache;
/// <see cref="RecoverCacheHit"/> repeats one to show the cost of a hit.
/// </remarks>
public class ECRecoverBenchmark
{
    private const int InputCount = 1024;
    private const int InputLength = 128;

    private byte[][] _inputs = [];

    // Only the gas columns read Input; the timed inputs are generated in GlobalSetup.
    public static IEnumerable<PrecompileBenchmarkBase.Param> Inputs()
    {
        yield return new(ECRecoverPrecompile.Instance, "distinct_valid_signatures", new byte[InputLength], null);
    }

    [ParamsSource(nameof(Inputs))]
    public PrecompileBenchmarkBase.Param Input { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        Random random = new(1);
        Ecdsa ecdsa = new();
        _inputs = new byte[InputCount][];
        for (int i = 0; i < InputCount; i++)
        {
            byte[] key = new byte[32];
            random.NextBytes(key);
            // keeps the key below the curve order
            key[0] = 1;
            PrivateKey privateKey = new(key);

            byte[] message = new byte[32];
            random.NextBytes(message);
            Signature signature = ecdsa.Sign(privateKey, new ValueHash256(message));

            byte[] input = new byte[InputLength];
            message.CopyTo(input, 0);
            input[63] = (byte)(Signature.VOffset + signature.RecoveryId);
            signature.Bytes.CopyTo(input.AsSpan(64));

            Result<byte[]> result = ECRecoverPrecompile.Instance.Run(input, Cancun.Instance);
            if (result.Data is not { Length: 32 } recovered || !recovered.AsSpan(12).SequenceEqual(privateKey.Address.Bytes))
                throw new InvalidOperationException($"{nameof(ECRecoverBenchmark)} input {i} does not recover its signer.");

            _inputs[i] = input;
        }
    }

    [Benchmark(OperationsPerInvoke = InputCount)]
    public bool Recover()
    {
        bool overallResult = true;
        foreach (byte[] input in _inputs)
            overallResult &= ECRecoverPrecompile.Instance.Run(input, Cancun.Instance);
        return overallResult;
    }

    [Benchmark]
    public bool RecoverCacheHit() => ECRecoverPrecompile.Instance.Run(_inputs[0], Cancun.Instance);
}
