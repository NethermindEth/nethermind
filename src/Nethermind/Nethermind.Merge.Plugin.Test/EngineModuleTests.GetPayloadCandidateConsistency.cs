// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public async Task GetPayload_describes_one_candidate_when_improvement_swaps_the_block_midway(int version)
    {
        IReleaseSpec spec = version switch
        {
            3 => Cancun.Instance,
            4 => Prague.Instance,
            5 => Osaka.Instance,
            _ => Amsterdam.Instance
        };
        // More candidates than any response should read, so no two reads can alias to one block.
        Block[] candidates = [BuildCandidate(blobCount: 1, spec), BuildCandidate(blobCount: 2, spec), BuildCandidate(blobCount: 3, spec), BuildCandidate(blobCount: 4, spec)];
        AlternatingBlockImprovementContextFactory factory = new(candidates);
        using MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: spec, configurer: builder =>
            builder.AddSingleton<IBlockImprovementContextFactory>(factory));

        string payloadId = chain.PayloadPreparationService.StartPreparingPayload(chain.BlockTree.Head!.Header, new PayloadAttributes
        {
            Timestamp = chain.BlockTree.Head.Timestamp + 1,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressF,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakE
        })!;

        IEngineRpcModule rpc = chain.EngineRpcModule;
        byte[] id = Bytes.FromHexString(payloadId);
        (Hash256 blockHash, byte[][] commitments, byte[][]? requests, UInt256 blockValue) = version switch
        {
            3 => Parts((await rpc.engine_getPayloadV3(id)).Data!),
            4 => Parts((await rpc.engine_getPayloadV4(id)).Data!),
            5 => Parts((await rpc.engine_getPayloadV5(id)).Data!),
            _ => Parts((await rpc.engine_getPayloadV6(id)).Data!)
        };

        Block served = Array.Find(candidates, c => c.Hash == blockHash)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(served, Is.Not.Null, "payload is not one of the candidates");
            Assert.That(factory.Created!.Reads, Is.LessThanOrEqualTo(candidates.Length), "reads wrapped around, so two reads may have aliased to one candidate");
            // engine_getPayloadV3 item 3: commitments must match the payload's blob versioned hashes.
            Assert.That(commitments, Is.EqualTo(new BlobsBundleV1(served).Commitments), "blobs bundle came from the other candidate");
            if (version >= 4)
            {
                Assert.That(requests, Is.EqualTo(served.ExecutionRequests), "execution requests came from the other candidate");
            }

            Assert.That(blockValue, Is.EqualTo(AlternatingBlockImprovementContext.FeesOf(Array.IndexOf(candidates, served))).Or.EqualTo(AlternatingBlockImprovementContext.FeesOf(Array.IndexOf(candidates, served) - 1)),
                "blockValue is neither the served candidate's fees nor the previous one's");
        }
    }

    private static (Hash256, byte[][], byte[][]?, UInt256) Parts(GetPayloadV3Result r) => (r.ExecutionPayload.BlockHash, r.BlobsBundle.Commitments, null, r.BlockValue);
    private static (Hash256, byte[][], byte[][]?, UInt256) Parts(GetPayloadV4Result r) => (r.ExecutionPayload.BlockHash, r.BlobsBundle.Commitments, r.ExecutionRequests, r.BlockValue);
    private static (Hash256, byte[][], byte[][]?, UInt256) Parts(GetPayloadV5Result r) => (r.ExecutionPayload.BlockHash, r.BlobsBundle.Commitments, r.ExecutionRequests, r.BlockValue);
    private static (Hash256, byte[][], byte[][]?, UInt256) Parts(GetPayloadV6Result r) => (r.ExecutionPayload.BlockHash, r.BlobsBundle.Commitments, r.ExecutionRequests, r.BlockValue);

    private static Block BuildCandidate(int blobCount, IReleaseSpec spec)
    {
        Transaction[] txs = new Transaction[blobCount];
        for (int i = 0; i < blobCount; i++)
        {
            txs[i] = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(1, spec: spec)
                .WithNonce((ulong)i)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;
        }

        Block block = Build.A.Block.WithTransactions(txs).WithGasUsed((ulong)blobCount).TestObject;
        block.ExecutionRequests = [[(byte)blobCount]];
        return block;
    }

    private class AlternatingBlockImprovementContextFactory(Block[] candidates) : IBlockImprovementContextFactory
    {
        public AlternatingBlockImprovementContext? Created { get; private set; }

        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader,
            PayloadAttributes payloadAttributes, DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts) =>
            Created = new AlternatingBlockImprovementContext(candidates, startDateTime);
    }

    /// <summary>
    /// Returns a different candidate on every read, standing in for an improvement that publishes
    /// between two reads. Any response built from more than one read therefore mixes candidates.
    /// The improvement task never completes, so the service schedules no reads of its own.
    /// </summary>
    private class AlternatingBlockImprovementContext(Block[] candidates, DateTimeOffset startDateTime) : IBlockImprovementContext
    {
        private int _reads;

        public static UInt256 FeesOf(int candidate) => (UInt256)(candidate + 1);

        /// <summary>How many times the block was read; above the candidate count, reads alias.</summary>
        public int Reads => Volatile.Read(ref _reads);

        public Block? CurrentBestBlock => candidates[(Interlocked.Increment(ref _reads) - 1) % candidates.Length];
        public UInt256 BlockFees => FeesOf((Math.Max(Volatile.Read(ref _reads), 1) - 1) % candidates.Length);
        public Task<Block?> ImprovementTask { get; } = new TaskCompletionSource<Block?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        public bool Disposed { get; private set; }
        public DateTimeOffset StartDateTime { get; } = startDateTime;

        public void CancelOngoingImprovements() { }
        public void Dispose() => Disposed = true;
    }
}
