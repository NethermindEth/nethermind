// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.JsonRpc;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    public enum PostExecutionField { StateRoot, ReceiptsRoot, GasUsed, LogsBloom }

    /// <summary>
    /// Guard, passes before and after the change: a block that fails post-execution validation while imported through
    /// the processing queue (as sync, the startup fixer or forkchoice reprocessing import it, never through newPayload)
    /// marks itself and its linked descendants INVALID with the right latestValidHash.
    /// </summary>
    [Test]
    public async Task Queue_imported_post_execution_failure_marks_the_block_and_descendants_invalid(
        [Values] PostExecutionField field,
        [Values] bool descendantsLinkedBeforeFailure)
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        await ProduceBranchV1(rpc, chain, 1, CreateParentBlockRequestOnHead(chain.BlockTree), setHead: true);
        Hash256 lastValid = chain.BlockTree.HeadHash!;

        ExecutionPayload[] payloads = await CreateBlockRequestBranch(chain, ExecutionPayload.Create(chain.BlockTree.Head!), Address.Zero, 3);
        ExecutionPayload b = payloads[0], c = payloads[1], d = payloads[2];
        switch (field)
        {
            case PostExecutionField.StateRoot: b.StateRoot = TestItem.KeccakA; break;
            case PostExecutionField.ReceiptsRoot: b.ReceiptsRoot = TestItem.KeccakA; break;
            case PostExecutionField.GasUsed: b.GasUsed += 1; break;
            case PostExecutionField.LogsBloom: b.LogsBloom = new Bloom(Enumerable.Repeat((byte)0xff, 256).ToArray()); break;
        }
        Rehash(b);
        c.ParentHash = b.BlockHash; Rehash(c);
        d.ParentHash = c.BlockHash; Rehash(d);

        Block blockB = b.TryGetBlock().Data!;
        Block blockC = c.TryGetBlock().Data!;
        Block blockD = d.TryGetBlock().Data!;

        IBlockValidator validator = chain.Container.Resolve<IBlockValidator>();
        IInvalidChainTracker tracker = chain.Container.Resolve<IInvalidChainTracker>();

        // BlockDownloader validates every downloaded block with ValidateSuggestedBlock before suggesting it.
        void LinkDescendants()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(validator.ValidateSuggestedBlock(blockC, blockB.Header, out string? e1), Is.True, e1);
                Assert.That(validator.ValidateSuggestedBlock(blockD, blockC.Header, out string? e2), Is.True, e2);
            }
        }

        Assert.That(validator.ValidateSuggestedBlock(blockB, chain.BlockTree.Head!.Header, out string? eb), Is.True, eb);
        if (descendantsLinkedBeforeFailure) LinkDescendants();

        Assert.That(await chain.BlockTree.SuggestBlockAsync(blockB), Is.EqualTo(AddBlockResult.Added));
        await chain.BlockProcessingQueue.WaitUntilRemovedAsync(blockB.Hash!).AsTask().WaitAsync(GateTimeout);

        if (!descendantsLinkedBeforeFailure) LinkDescendants();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(lastValid), "head must not move to the invalid block");
            foreach (Block blk in new[] { blockB, blockC, blockD })
            {
                Assert.That(tracker.IsOnKnownInvalidChain(blk.Hash!, out Hash256? lvh), Is.True, $"{blk.Number} must be on a known invalid chain");
                Assert.That(lvh, Is.EqualTo(lastValid), $"{blk.Number} latestValidHash");
            }
            Assert.That(tracker.IsOnKnownInvalidChain(lastValid, out _), Is.False, "the valid ancestor must stay valid");
        }

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(d.BlockHash, Keccak.Zero, Keccak.Zero));
        ResultWrapper<PayloadStatusV1> np = await rpc.engine_newPayloadV1(d);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(fcu.Data.PayloadStatus.LatestValidHash, Is.EqualTo(lastValid));
            Assert.That(np.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(np.Data.LatestValidHash, Is.EqualTo(lastValid));
        }

        static void Rehash(ExecutionPayload p)
        {
            TryCalculateHash(p, out Hash256 h);
            p.BlockHash = h;
        }
    }
}
