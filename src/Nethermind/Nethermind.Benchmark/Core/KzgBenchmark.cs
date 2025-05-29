// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Crypto;
using System;
using System.Threading.Tasks;

namespace Nethermind.Benchmarks.Core;

public class KzgBenchmark
{
    ShardBlobNetworkWrapper wrapper = null;
    IBlobProofsManager manager = null;

    [Params(1, 2, 3, 6, 24, 48, 72)]
    public int Blobas { get; set; }

    [GlobalSetup(Target = nameof(VerifyCkzg))]
    public async Task SetupCkzg()
    {
        await KzgPolynomialCommitments.InitializeAsync();
        wrapper = AllocateWrapper(manager = new BlobProofsManagerV1(), Blobas);
    }

    [GlobalSetup(Target = nameof(VerifyEthKzg))]
    public void SetupEthKzg()
    {
        wrapper = AllocateWrapper(manager = new EthKzgBlobProofsManagerV1(), Blobas);
    }


    //[GlobalCleanup(Target = nameof(VerifyCkzg))]
    //public void CleanupCkzg()
    //{
    //}

    //[GlobalCleanup(Target = nameof(VerifyEthKzg))]
    //public void CleanupEthKzg()
    //{
    //}

    private static ShardBlobNetworkWrapper AllocateWrapper(IBlobProofsManager manager, int blobCount)
    {
        Random random = new(42);
        var blobs = new byte[blobCount][];
        for (int i = 0; i < blobs.Length; i++)
        {
            blobs[i] = new byte[Ckzg.BytesPerBlob];
            random.NextBytes(blobs[i]);
            for (var j = 0; j < blobs[i].Length; j += Ckzg.BytesPerBlob)
            {
                blobs[i][j] %= 254;
            }
        }

        return manager.AllocateWrapper(blobs);
    }

    [Benchmark]
    public void VerifyCkzg()
    {
        manager.ValidateProofs(wrapper);
    }

    [Benchmark]
    public void VerifyEthKzg()
    {
        manager.ValidateProofs(wrapper);
    }
}
