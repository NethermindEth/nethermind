// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    /// <summary>
    /// A payload large enough for its transactions root to be computed in the background answers the same whether
    /// the root or sender recovery takes the workers first, and whether its Received line is written before the
    /// hash check or after the hand-off.
    /// </summary>
    [Test]
    public async Task newPayloadV1_with_a_background_transactions_root_answers_the_same_in_either_order(
        [Values] bool txRootFirst, [Values] bool logAfterEnqueue)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(null, new MergeConfig
        {
            TerminalTotalDifficulty = "0",
            NewPayloadTxRootFirst = txRootFirst,
            NewPayloadLogAfterEnqueue = logAfterEnqueue
        });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayload parent = CreateParentBlockRequestOnHead(chain.BlockTree);
        Transaction[] transactions = BuildTransactions(chain, parent.BlockHash, TestItem.PrivateKeyB, TestItem.AddressF, 40, 10, out _, out _);
        chain.AddTransactions(transactions);

        Task improved = chain.WaitForImprovedBlock(parent.BlockHash, transactions.Length);
        string payloadId = (await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(parent.BlockHash, Keccak.Zero, Keccak.Zero),
            new PayloadAttributes { Timestamp = parent.Timestamp + 12, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })).Data.PayloadId!;
        await improved;
        ExecutionPayload payload = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;
        Assert.That(payload.Transactions, Has.Length.EqualTo(transactions.Length), "precondition: enough transactions for the background root");

        Hash256 blockHash = payload.BlockHash;
        payload.BlockHash = TestItem.KeccakB;
        // A fresh array drops the decoded transactions and their root, as a payload off the wire has neither.
        payload.Transactions = [.. payload.Transactions];
        Assert.That((await rpc.engine_newPayloadV1(payload)).Data.Status, Is.EqualTo(PayloadStatus.Invalid));

        payload.BlockHash = blockHash;
        payload.Transactions = [.. payload.Transactions];
        Assert.That((await rpc.engine_newPayloadV1(payload)).Data.Status, Is.EqualTo(PayloadStatus.Valid));

        // A single processor computes the root inline and recovers nothing early, so there is no order to see.
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) return;
        BlockDiag diag = payload.Diag!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(diag.Get(BlockDiag.P.HTxRoot), Is.Not.Zero, "the root was computed in the background");
            Assert.That(diag.Get(BlockDiag.P.HRecovery), Is.Not.Zero, "recovery was started");
            Assert.That(diag.Get(BlockDiag.P.HRecovery) > diag.Get(BlockDiag.P.HTxRoot), Is.EqualTo(txRootFirst),
                "recovery starts after the transactions root exactly when the root goes first");
        }
    }
}
