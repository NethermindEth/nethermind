// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>Shared setup for the blob-carrying frame transaction measurements: the samples they read back, the
/// best-of-N timing pass, and the report file the test runner would otherwise swallow.</summary>
/// <remarks>Both measurements read the same kind of record in different places — one from blob storage under the
/// head lock, one from the pool on the eth/72 serve path — so the samples must be built the same way for their
/// figures to be comparable at all.</remarks>
internal static class FrameTxBlobMeasurementHarness
{
    internal const int SampleTxs = 256;
    internal const int TimedPasses = 5;

    internal static void EnsureKzgInitialized()
    {
        if (!KzgPolynomialCommitments.IsInitialized) KzgPolynomialCommitments.InitializeAsync().Wait();
    }

    /// <summary>Builds <see cref="SampleTxs"/> blob-carrying frame transactions into <paramref name="samples"/>.</summary>
    /// <remarks>One sidecar is shared by every sample: the blobs differ only in bytes the decode does not branch
    /// on, and computing cell proofs per transaction would dominate the setup.</remarks>
    internal static void BuildSamples(int blobsPerTx, List<Transaction> samples)
    {
        samples.Clear();
        ShardBlobNetworkWrapper wrapper = BuildWrapper(blobsPerTx, out byte[][] versionedHashes);
        for (int i = 0; i < SampleTxs; i++)
        {
            samples.Add(BuildBlobFrameTx(TestItem.Addresses[i % TestItem.Addresses.Length], (ulong)i, wrapper, versionedHashes));
        }
    }

    /// <summary>Runs <paramref name="pass"/> once discarded, then <see cref="TimedPasses"/> times.</summary>
    /// <remarks>
    /// Without the discarded pass the read that runs first is charged with JIT-compiling a decoder the second
    /// then finds warm, which lands on whichever side the caller happens to time first rather than on the one
    /// that is genuinely slower. The best pass is the reported figure and the worst is returned beside it, so a
    /// spread wide enough to swallow the difference is visible rather than averaged away. The allocation figure
    /// is taken from that same best pass, so the two reported numbers describe one run rather than two.
    /// </remarks>
    internal static (TimeSpan Best, TimeSpan Worst, long Allocated) Measure(Func<(TimeSpan, long)> pass)
    {
        pass();

        TimeSpan best = TimeSpan.MaxValue;
        TimeSpan worst = TimeSpan.Zero;
        long bestAllocated = 0;
        for (int i = 0; i < TimedPasses; i++)
        {
            (TimeSpan elapsed, long passAllocated) = pass();
            if (elapsed < best) (best, bestAllocated) = (elapsed, passAllocated);
            if (elapsed > worst) worst = elapsed;
        }

        return (best, worst, bestAllocated);
    }

    /// <summary>Appends <paramref name="report"/> to the file <paramref name="outVariable"/> names, or to
    /// <paramref name="defaultFileName"/> in the temp directory.</summary>
    internal static void WriteReport(StringBuilder report, string outVariable, string defaultFileName)
    {
        if (report.Length == 0) return;

        string path = Environment.GetEnvironmentVariable(outVariable) ?? Path.Combine(Path.GetTempPath(), defaultFileName);
        File.AppendAllText(path, report.ToString());
        TestContext.Out.WriteLine(report.ToString());
    }

    private static ShardBlobNetworkWrapper BuildWrapper(int blobCount, out byte[][] versionedHashes)
    {
        IBlobProofsManager proofsManager = IBlobProofsManager.For(ProofVersion.V1);
        byte[][] rawBlobs = new byte[blobCount][];
        for (int i = 0; i < blobCount; i++)
        {
            byte[] blob = new byte[Ckzg.BytesPerBlob];
            blob[0] = (byte)(i % 256);
            rawBlobs[i] = blob;
        }

        ShardBlobNetworkWrapper wrapper = proofsManager.AllocateWrapper(rawBlobs);
        proofsManager.ComputeProofsAndCommitments(wrapper);
        versionedHashes = proofsManager.ComputeHashes(wrapper);
        return wrapper;
    }

    private static Transaction BuildBlobFrameTx(Address sender, ulong nonce, ShardBlobNetworkWrapper wrapper, byte[][] versionedHashes)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            SenderAddress = sender,
            Nonce = nonce,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 1.GWei,
            MaxFeePerBlobGas = 1.GWei,
            Frames = [FrameTxTestFrames.OnlyVerify(gasLimit: 40_000), FrameTxTestFrames.Pay(TestItem.AddressF, gasLimit: 40_000)],
            FrameSignatures = [],
            BlobVersionedHashes = versionedHashes,
            NetworkWrapper = wrapper,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
