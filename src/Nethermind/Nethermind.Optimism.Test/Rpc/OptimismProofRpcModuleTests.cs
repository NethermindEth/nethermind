// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.OverridableEnv;
using Nethermind.State.Proofs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

[Parallelizable(ParallelScope.All)]
public class OptimismProofRpcModuleTests
{
    private static readonly Hash256 TxHash = TestItem.KeccakA;
    private static readonly Hash256 BlockHash = TestItem.KeccakB;

    [Test, NonParallelizable]
    public void Pre_canyon_deposit_receipt_proof_matches_the_header_root()
    {
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder());
        Transaction tx = Build.A.Transaction.WithType(TxType.DepositTx).WithHash(TxHash).WithSourceHash(TestItem.KeccakC)
            .WithSenderAddress(TestItem.AddressA).WithTo(TestItem.AddressB).TestObject;
        Block block = Build.A.Block.WithTransactions(tx).TestObject;
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.GetNonce(TestItem.AddressA).Returns(5UL);
        using OptimismBlockReceiptTracer receiptTracer = new(Substitute.For<IOptimismSpecHelper>(), worldState);
        receiptTracer.StartNewBlockTrace(block);
        receiptTracer.StartNewTxTrace(tx);
        receiptTracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(21_000, 21_000), [], []);
        receiptTracer.EndTxTrace();
        OptimismTxReceipt receipt = (OptimismTxReceipt)receiptTracer.LastReceipt;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.DepositNonce, Is.EqualTo(4UL));
            Assert.That(receipt.DepositReceiptVersion, Is.Null);
        }
        TxReceipt[] receipts = [receipt];

        OptimismReceiptTrieDecoder trieDecoder = new();
        block.Header.ReceiptsRoot = ReceiptTrie.CalculateRoot(London.Instance, receipts, trieDecoder);
        IReceiptFinder receiptFinder = ReceiptFinderReturning(receipt);
        receiptFinder.FindBlockHash(TxHash).Returns(block.Hash);
        receiptFinder.Get(block).Returns(receipts);
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);

        ITracer tracer = Substitute.For<ITracer>();
        tracer.TraceReceipts(block).Returns(receipts);
        IOverridableEnv<ITracer> tracerEnv = Substitute.For<IOverridableEnv<ITracer>>();
        tracerEnv.TryBuildAndOverrideAtTarget(block.Header, null, null, out _).Returns(call =>
        {
            call[3] = new Scope<ITracer>(tracer, Substitute.For<IDisposable>());
            return true;
        });

        RlpDecoderKey key = new(typeof(TxReceipt), RlpDecoderKey.Trie);
        IRlpDecoder<TxReceipt> original = Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie);
        RlpDecoderKey defaultKey = new(typeof(TxReceipt));
        IRlpDecoder<TxReceipt> originalDefault = Rlp.GetDecoderOrThrow<TxReceipt>();
        Rlp.RegisterDecoder(key, trieDecoder);
        Rlp.RegisterDecoder(defaultKey, new OptimismReceiptMessageDecoder());
        try
        {
            ProofRpcModule module = new(tracerEnv, Substitute.For<IBlockchainBridge>(), blockFinder, receiptFinder,
                new TestSpecProvider(London.Instance), Substitute.For<IPoSSwitcher>(), new JsonRpcConfig());
            ReceiptWithProof? proof = module.proof_getTransactionReceipt(TxHash, false).Data;
            Hash256? proofRoot = proof?.ReceiptProof is { Length: > 0 } nodes ? Keccak.Compute(nodes[0]) : null;

            Assert.That(proofRoot, Is.EqualTo(block.Header.ReceiptsRoot));
        }
        finally
        {
            Rlp.RegisterDecoder(key, original);
            Rlp.RegisterDecoder(defaultKey, originalDefault);
        }
    }

    [Test]
    public void Deposit_transaction_takes_its_nonce_and_receipt_version_from_the_receipt()
    {
        DepositTransactionForRpc deposit = new() { BlockHash = BlockHash };
        IReceiptFinder receiptFinder = ReceiptFinderReturning(new OptimismTxReceipt { TxHash = TxHash, DepositNonce = 7, DepositReceiptVersion = 1 });

        ResultWrapper<TransactionForRpcWithProof?> result = CreateModule(deposit, receiptFinder).proof_getTransactionByHash(TxHash, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data!.Transaction, Is.SameAs(deposit));
            Assert.That(deposit.Nonce, Is.EqualTo((UInt256)7));
            Assert.That(deposit.DepositReceiptVersion, Is.EqualTo((UInt256)1));
        }
    }

    /// <remarks>
    /// Without the receipt the deposit nonce is unknown, and serving it as zero would be indistinguishable from a real one.
    /// </remarks>
    [Test]
    public void Deposit_transaction_without_a_receipt_returns_null_result([Values] bool storedReceiptIsNotOptimism)
    {
        DepositTransactionForRpc deposit = new() { BlockHash = BlockHash };
        // Either no receipt for this transaction, or one with the right hash that carries no deposit fields.
        TxReceipt stored = storedReceiptIsNotOptimism
            ? new TxReceipt { TxHash = TxHash }
            : new OptimismTxReceipt { TxHash = TestItem.KeccakC };
        IReceiptFinder receiptFinder = ReceiptFinderReturning(stored);

        ResultWrapper<TransactionForRpcWithProof?> result = CreateModule(deposit, receiptFinder).proof_getTransactionByHash(TxHash, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    public void Non_deposit_transaction_is_served_without_a_receipt_lookup()
    {
        TransactionForRpc legacy = new LegacyTransactionForRpc { BlockHash = BlockHash };
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

        ResultWrapper<TransactionForRpcWithProof?> result = CreateModule(legacy, receiptFinder).proof_getTransactionByHash(TxHash, false);

        Assert.That(result.Data!.Transaction, Is.SameAs(legacy));
        receiptFinder.DidNotReceiveWithAnyArgs().Get(Arg.Any<Hash256>());
        receiptFinder.DidNotReceiveWithAnyArgs().Get(Arg.Any<Block>());
    }

    private static OptimismProofRpcModule CreateModule(TransactionForRpc served, IReceiptFinder receiptFinder)
    {
        IProofRpcModule inner = Substitute.For<IProofRpcModule>();
        inner.proof_getTransactionByHash(TxHash, false)
            .Returns(ResultWrapper<TransactionForRpcWithProof?>.Success(new TransactionForRpcWithProof { Transaction = served }));
        return new OptimismProofRpcModule(inner, receiptFinder);
    }

    private static IReceiptFinder ReceiptFinderReturning(TxReceipt receipt)
    {
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(BlockHash).Returns([receipt]);
        return receiptFinder;
    }
}
