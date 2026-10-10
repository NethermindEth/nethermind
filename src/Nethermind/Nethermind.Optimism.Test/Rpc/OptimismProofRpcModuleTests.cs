// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Optimism.Rpc;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

[Parallelizable(ParallelScope.All)]
public class OptimismProofRpcModuleTests
{
    private static readonly Hash256 TxHash = TestItem.KeccakA;
    private static readonly Hash256 BlockHash = TestItem.KeccakB;

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
