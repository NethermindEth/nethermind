// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    /// <summary>Gas of a value transfer to a cold recipient: 21,000 per the EIP-2780 reference table.</summary>
    private const ulong IncludedTransferGas = 21_000;

    /// <summary>
    /// A transaction the producer executes and then drops must leave no trace in the block access list, and the
    /// included transactions and the post-execution withdrawal must keep contiguous indices; the node's own
    /// newPayload re-executes the block in validation mode, so a stale or misindexed entry fails the payload.
    /// </summary>
    [TestCase(true, null, TestName = "GetPayloadV6_purges_transaction_discarded_after_execution_from_BAL(discarded first)")]
    [TestCase(false, null, TestName = "GetPayloadV6_purges_transaction_discarded_after_execution_from_BAL(discarded last)")]
    public virtual async Task GetPayloadV6_purges_transaction_discarded_after_execution_from_BAL(bool discardedFirst, string? customWithdrawalContractAddress)
    {
        Transaction? discarded = null;
        DiscardingTransactionProcessorFactory factory = new(tx => tx.Hash == discarded?.Hash);
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance,
            configurer: builder => builder.AddSingleton<ITransactionProcessorFactory>(factory));

        // Writes storage and transient storage, moves value to a fresh account and reads another, then deploys.
        byte[] initCode = Prepare.EvmCode
            .PushData(7).PushData(0).Op(Instruction.SSTORE)
            .PushData(1).PushData(1).Op(Instruction.TSTORE)
            .CallWithValue(TestItem.AddressD, 50_000, 3).Op(Instruction.POP)
            .PushData(TestItem.AddressB).Op(Instruction.BALANCE).Op(Instruction.POP)
            .ForInitOf([(byte)Instruction.STOP])
            .Done;
        discarded = Eip1559Tx(TestItem.PrivateKeyC, null, 0, discardedFirst ? 100ul : 5ul, 1_000_000, initCode, value: 10);
        Transaction[] included =
        [
            Eip1559Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 10, 100_000),
            Eip1559Tx(TestItem.PrivateKeyA, TestItem.AddressB, 1, 10, 100_000),
            Eip1559Tx(TestItem.PrivateKeyB, TestItem.AddressC, 0, 10, 100_000),
        ];
        foreach (Transaction tx in included.Prepend(discarded))
        {
            Assert.That(chain.TxPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }

        Withdrawal withdrawal = new() { Index = 0, ValidatorIndex = 0, Address = TestItem.AddressF, AmountInGwei = 1 };
        GetPayloadV6Result result = await BuildPayloadV6(chain, included.Length, withdrawal);
        ExecutionPayloadV4 payload = result.ExecutionPayload;

        uint postExecutionIndex = (uint)included.Length + 1;
        ReadOnlyBlockAccessList bal = Rlp.Decode<ReadOnlyBlockAccessList>(new Rlp(payload.BlockAccessList!))!;
        Address createdContract = ContractAddress.From(TestItem.AddressC, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.DiscardedCount, Is.GreaterThan(0), "the discarded transaction never executed");
            Assert.That(payload.Transactions, Has.Length.EqualTo(included.Length));
            Assert.That(payload.GasUsed, Is.EqualTo((ulong)included.Length * IncludedTransferGas));
            Assert.That(bal.GetAccountChanges(TestItem.AddressD), Is.Null);
            Assert.That(bal.GetAccountChanges(createdContract), Is.Null);
            ReadOnlyAccountChanges discardedSender = bal.GetAccountChanges(TestItem.AddressC)!;
            Assert.That(discardedSender.NonceChanges, Is.Empty);
            Assert.That(discardedSender.BalanceChanges.Select(static c => c.Index), Is.EqualTo(new[] { (uint)included.Length }));
            if (customWithdrawalContractAddress is null)
            {
                Assert.That(bal.GetAccountChanges(TestItem.AddressF)!.BalanceChanges.Select(static c => c.Index), Is.EqualTo(new[] { postExecutionIndex }));
                Assert.That(ChangeIndices(bal).Max(), Is.EqualTo(postExecutionIndex));
            }
            else
            {
                // AuRa pays withdrawals through a system call to the withdrawal contract, which has no code in tests.
                Assert.That(bal.GetAccountChanges(new Address(customWithdrawalContractAddress)), Is.Not.Null);
                Assert.That(bal.GetAccountChanges(TestItem.AddressF), Is.Null);
                Assert.That(ChangeIndices(bal).Max(), Is.EqualTo((uint)included.Length));
            }
        }

        await AssertProducedPayloadValidates(chain, result);
    }

    /// <summary>Re-executes a produced payload through the node's own newPayload (validation mode, BAL checked).</summary>
    private static async Task AssertProducedPayloadValidates(MergeTestBlockchain chain, GetPayloadV6Result result)
    {
        ResultWrapper<PayloadStatusV1> status = await chain.EngineRpcModule.engine_newPayloadV5(
            result.ExecutionPayload, [], TestItem.KeccakE, result.ExecutionRequests);
        Assert.That(status.Data.Status, Is.EqualTo(PayloadStatus.Valid), status.Data.ValidationError);
    }

    private static async Task<GetPayloadV6Result> BuildPayloadV6(MergeTestBlockchain chain, int minTransactions, params Withdrawal[] withdrawals)
    {
        BlockHeader head = chain.BlockTree.Head!.Header;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = head.Timestamp + 12,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressE,
            Withdrawals = withdrawals,
            ParentBeaconBlockRoot = TestItem.KeccakE,
            SlotNumber = head.SlotNumber + 1,
            TargetGasLimit = head.GasLimit
        };
        ForkchoiceStateV1 forkchoiceState = new(head.Hash!, head.Hash!, head.Hash!);
        Task improved = chain.WaitForImprovedBlock(head.Hash!, minTransactions);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV4(forkchoiceState, payloadAttributes);
        Assert.That(fcu.Result.ResultType, Is.EqualTo(ResultType.Success));
        await improved;

        ResultWrapper<GetPayloadV6Result?> payload = await chain.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
        Assert.That(payload.Data, Is.Not.Null);
        return payload.Data!;
    }

    private static Transaction Eip1559Tx(PrivateKey key, Address? to, ulong nonce, ulong priorityFee, ulong gasLimit, byte[]? initCode = null, ulong value = 1) =>
        (initCode is null ? Build.A.Transaction.WithTo(to) : Build.A.Transaction.WithCode(initCode))
            .WithType(TxType.EIP1559)
            .WithNonce(nonce)
            .WithValue(value)
            .WithMaxFeePerGas(9.GWei)
            .WithMaxPriorityFeePerGas(priorityFee)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(key)
            .TestObject;

    private static List<uint> ChangeIndices(ReadOnlyBlockAccessList bal)
    {
        List<uint> indices = [];
        foreach (ReadOnlyAccountChanges account in bal.AccountChanges)
        {
            indices.AddRange(account.BalanceChanges.Select(static c => c.Index));
            indices.AddRange(account.NonceChanges.Select(static c => c.Index));
            indices.AddRange(account.CodeChanges.Select(static c => c.Index));
            indices.AddRange(account.StorageChanges.SelectMany(static s => s.Changes.Select(static c => c.Index)));
        }
        return indices;
    }

    /// <summary>Builds transaction processors that run a selected transaction in full during block building, then reject it.</summary>
    private sealed class DiscardingTransactionProcessorFactory(Func<Transaction, bool> discard) : ITransactionProcessorFactory
    {
        private readonly Func<Transaction, bool> _discard = discard;
        private int _discardedCount;
        public int DiscardedCount => Volatile.Read(ref _discardedCount);

        public ITransactionProcessor Create(
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ISpecProvider specProvider,
            IWorldState worldState,
            IVirtualMachine virtualMachine,
            ICodeInfoRepository codeInfoRepository,
            ILogManager logManager,
            bool parallel) =>
            new DiscardingTransactionProcessor(this, blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);

        /// <remarks>
        /// Fails the refund with <see cref="TransactionResult.ErrorType.StateGasInvariantViolated"/>, the one failure the
        /// processor raises after mutating state in block building, at the point a real violation is raised: after
        /// execution, before the receipt and the block gas are accounted, so the processor's own snapshot restore runs.
        /// </remarks>
        private sealed class DiscardingTransactionProcessor(
            DiscardingTransactionProcessorFactory factory,
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ISpecProvider specProvider,
            IWorldState worldState,
            IVirtualMachine virtualMachine,
            ICodeInfoRepository codeInfoRepository,
            ILogManager logManager,
            bool parallel)
            : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)
        {
            protected override RefundResult Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
                in TransactionSubstate substate, in EthereumGasPolicy unspentGas, in UInt256 gasPrice, ulong codeInsertRefunds,
                in EthereumGasPolicy floorGas, in EthereumGasPolicy intrinsicGasStandard, long postIntrinsicStateReservoir, bool topLevelCreateStateGasCharged = false)
            {
                RefundResult result = base.Refund(tx, header, spec, opts, in substate, in unspentGas, in gasPrice, codeInsertRefunds,
                    in floorGas, in intrinsicGasStandard, postIntrinsicStateReservoir, topLevelCreateStateGasCharged);
                if (opts != ExecutionOptions.BuildUp || !factory._discard(tx)) return result;

                Interlocked.Increment(ref factory._discardedCount);
                return RefundResult.Invalid(TransactionResult.ErrorType.StateGasInvariantViolated.WithDetail("discarded after execution by test"));
            }
        }
    }
}
