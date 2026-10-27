// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezL2SystemCallsTests
{
    private static readonly NativeHistorySchedule HistorySchedule = new(120, 2, 6, 2);
    private static readonly NativeHistorySource HistorySource = new(1, 1, TestItem.KeccakA.ValueHash256);
    private static readonly string[] InvalidHistoryEnvelopeFields =
        ["source", "chain", "gas", "value", "nonce", "sender", "calldata", "later-hash", "cutoff", "missing", "later", "duplicate", "randao", "off-schedule"];
    private static readonly string[] InvalidHistoryParentFields =
        ["code", "nonce", "first", "endpoint", "next", "interval", "delay", "max", "wide", "hash"];

    [TestCase(false, 0, TestName = "History_InheritedOrigin_ReachesApplicationOpcode")]
    [TestCase(true, 0, TestName = "History_EmptyRepeat_AdvancesCutoffAndRetainsOrigin")]
    [TestCase(true, 1, TestName = "History_RecordedImport_UpdatesStorageAndApplicationOpcode")]
    [TestCase(true, 2, TestName = "History_MultipleRecords_VerifiesEachHashAndUsesFinalOrigin")]
    public void Process_NativeHistory_ExecutesRuntimeAndSetsApplicationRandomness(bool updating, int count) =>
        WithHistory((scope, state, header, chain) =>
        {
            Block block = HistoryBlock(header, chain, updating, count);
            NativeHistoryBlockExecutor executor = HistoryExecutor(scope, state, chain);
            // A previously cached context and a decoded header's merge metadata must not select the VM seed.
            header.IsPostMerge = false;
            header.Difficulty = 7;
            header.MixHash = TestItem.KeccakC;
            executor.SetBlockExecutionContext(new BlockExecutionContext(header, Osaka.Instance));
            header.MixHash = new Hash256(chain.At(99UL + (ulong)count).PrevRandao);
            TxReceipt[] receipts = ExecuteHistory(executor, block);
            state.Get(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)2), out UInt256 endpoint);
            state.Get(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)3), out UInt256 nextCutoff);
            state.Get(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(99UL + (ulong)count)), out UInt256 hash);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(endpoint, Is.EqualTo((UInt256)(99 + count)));
                Assert.That(nextCutoff, Is.EqualTo((UInt256)(updating ? 132 : 120)));
                Assert.That(hash.ToValueHash(), Is.EqualTo(chain.At(99UL + (ulong)count).Hash));
                Assert.That(ReadStorage(state, TestItem.AddressB).ToValueHash(), Is.EqualTo(chain.At(99UL + (ulong)count).PrevRandao),
                    "the application must execute PREVRANDAO with the selected L1 mixHash");
                Assert.That(receipts.Length, Is.EqualTo(updating ? 2 : 1));
                Assert.That(receipts[^1].StatusCode, Is.EqualTo(StatusCode.Success));
                Assert.That(state.GetNonce(Address.SystemUser), Is.Zero);
                Assert.That(state.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(1.Ether - receipts[^1].GasUsed * 2.GWei));
                Assert.That(header.GasUsed, Is.EqualTo(receipts[^1].GasUsedTotal));
                Assert.That(header.MixHash!.ValueHash256, Is.EqualTo(chain.At(99UL + (ulong)count).PrevRandao));
                if (updating)
                {
                    Assert.That(receipts[0].TxType, Is.EqualTo(EezConstants.NativeSystemTxType));
                    Assert.That(receipts[0].StatusCode, Is.EqualTo(StatusCode.Success));
                    Assert.That(receipts[0].Logs, Is.Empty);
                    Assert.That(receipts[0].GasUsed, Is.GreaterThan(21_000UL).And.LessThan(EezConstants.L1HistoryTxGasCap));
                    Assert.That(receipts[^1].GasUsedTotal, Is.EqualTo(receipts[0].GasUsed + receipts[1].GasUsed));
                }
            }
        });

    [Test]
    public void Process_NativeHistoryInvalidEnvelope_RejectsBeforeApplication(
        [ValueSource(nameof(InvalidHistoryEnvelopeFields))] string field) =>
        WithHistory((scope, state, header, chain) =>
        {
            Block block = HistoryBlock(header, chain, true, field == "later-hash" ? 2 : 1);
            Transaction import = block.Transactions[0];
            switch (field)
            {
                case "source": import.SourceHash = TestItem.KeccakC; break;
                case "chain": import.ChainId = 2; break;
                case "gas": import.GasLimit--; break;
                case "value": import.Value = 1; break;
                case "nonce": import.Nonce = 1; break;
                case "sender": import.SenderAddress = TestItem.AddressC; break;
                case "calldata":
                case "later-hash":
                case "cutoff":
                    byte[] changed = import.Data.ToArray();
                    changed[field == "cutoff" ? 35 : changed.Length - 1] ^= 1;
                    import.Data = changed;
                    import.SourceHash = new Hash256(HistorySource.Compute(header.ParentHash!.ValueHash256, header.Number, header.Timestamp, changed));
                    break;
                case "missing": block = new Block(header, [block.Transactions[1]], []); break;
                case "later": block = new Block(header, [block.Transactions[1], import], []); break;
                case "duplicate": block = new Block(header, [import, import, block.Transactions[1]], []); break;
                case "randao": header.MixHash = TestItem.KeccakC; break;
                case "off-schedule": header.Timestamp = 128; break;
            }
            AssertHistoryRejected(HistoryExecutor(scope, state, chain), block, state);
        });

    [Test]
    public void Process_NativeHistoryWrongEntryContext_RejectsBeforeApplication([Values("legacy", "genesis", "used-gas")] string field) =>
        WithHistory((scope, state, header, chain) =>
        {
            Block block = HistoryBlock(header, chain, false, 0);
            if (field == "genesis") header.Number = 0;
            if (field == "used-gas") header.GasUsed = 1;
            NativeHistoryBlockExecutor executor = new(scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>(), state,
                scope.Resolve<ISpecProvider>(), field == "legacy" ? EezChainRules.Rollup0 : NativeRules(), HistorySchedule, 99, HistorySource, chain);
            Assert.That(() => ExecuteHistory(executor, block), Throws.TypeOf<InvalidBlockException>()
                .With.Message.EqualTo("Native history requires a fresh native-profile block."));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadStorage(state, TestItem.AddressB), Is.EqualTo(UInt256.Zero));
                Assert.That(state.GetNonce(TestItem.PrivateKeyA.Address), Is.Zero);
                Assert.That(header.GasUsed, Is.EqualTo(field == "used-gas" ? 1UL : 0UL));
            }
        });

    [Test]
    public void Process_NativeHistoryInvalidParent_RejectsBeforeApplication(
        [ValueSource(nameof(InvalidHistoryParentFields))] string field) =>
        WithHistory((scope, state, header, chain) =>
        {
            switch (field)
            {
                case "code": state.InsertCode(EezConstants.L1HistoryAddress, StopCode, Osaka.Instance); break;
                case "nonce": state.SetNonce(EezConstants.L1HistoryAddress, 2); break;
                case "first": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)1), 98); break;
                case "endpoint": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)2), 101); break;
                case "next": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)3), 132); break;
                case "interval": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)4), 24); break;
                case "delay": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)5), 8); break;
                case "max": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)6), 63); break;
                case "wide": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)2), UInt256.MaxValue); break;
                case "hash": state.Set(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(99)), 0); break;
            }
            state.Commit(Osaka.Instance);
            AssertHistoryRejected(HistoryExecutor(scope, state, chain), HistoryBlock(header, chain, true, 1), state);
        });

    [Test]
    public void Process_NativeHistoryRuntimeRevert_InvalidatesBlock()
        => WithHistory((scope, state, header, chain) =>
        {
            state.Set(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(100)), 1);
            state.Commit(Osaka.Instance);
            NativeHistoryBlockExecutor executor = HistoryExecutor(scope, state, chain);
            Block block = HistoryBlock(header, chain, true, 1);
            Assert.That(() => ExecuteHistory(executor, block), Throws.TypeOf<InvalidBlockException>()
                .With.Message.EqualTo("Mandatory native history import failed or emitted logs."));
            state.Get(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)2), out UInt256 endpoint);
            Assert.That(endpoint, Is.EqualTo((UInt256)99), "a reverted EVM import must not advance the endpoint");
        });

    private static void AssertHistoryRejected(NativeHistoryBlockExecutor executor, Block block, IWorldState state)
    {
        Exception? error = Assert.Catch(() => ExecuteHistory(executor, block));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error, Is.TypeOf<InvalidBlockException>().Or.TypeOf<EezSettlementException>());
            Assert.That(ReadStorage(state, TestItem.AddressB), Is.EqualTo(UInt256.Zero));
            Assert.That(state.GetNonce(TestItem.PrivateKeyA.Address), Is.Zero);
            Assert.That(block.GasUsed, Is.Zero);
        }
    }

    private static TxReceipt[] ExecuteHistory(NativeHistoryBlockExecutor executor, Block block)
    {
        using BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        return executor.ProcessTransactions(block, ProcessingOptions.None, tracer);
    }

    private static Block HistoryBlock(BlockHeader header, NativeL1HeaderChain chain, bool updating, int count)
    {
        header.Timestamp = updating ? 126UL : 122UL;
        header.ParentHash = TestItem.KeccakD;
        header.MixHash = new Hash256(chain.At(99UL + (ulong)count).PrevRandao);
        Transaction app = Build.A.Transaction.WithChainId(1).WithTo(TestItem.AddressB).WithValue(0).WithGasLimit(100_000)
            .WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        if (!updating) return new Block(header, [app], []);
        byte[] input = NativeHistoryCall.Encode(120, chain, 99, 99UL + (ulong)count);
        Hash256 sourceHash = new(HistorySource.Compute(header.ParentHash.ValueHash256, header.Number, header.Timestamp, input));
        Transaction import = NativeTransaction(0, EezConstants.L1HistoryTxGasCap, EezConstants.L1HistoryAddress, input, sourceHash);
        return new Block(header, [import, app], []);
    }

    private static NativeHistoryBlockExecutor HistoryExecutor(ILifetimeScope scope, IWorldState state, NativeL1HeaderChain chain) =>
        new(scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>(), state, scope.Resolve<ISpecProvider>(), NativeRules(),
            HistorySchedule, 99, HistorySource, chain);

    private static void WithHistory(Action<ILifetimeScope, IWorldState, BlockHeader, NativeL1HeaderChain> action, bool contractV3 = false)
    {
        NativeL1HeaderChain chain = new();
        Hash256 parent = TestItem.KeccakC;
        for (int index = 0; index < 3; index++)
        {
            Hash256 randao = index switch { 0 => TestItem.KeccakA, 1 => TestItem.KeccakB, _ => TestItem.KeccakC };
            BlockHeader l1 = Build.A.BlockHeader.WithNumber(99 + index).WithTimestamp(108UL + (ulong)index * 6)
                .WithParentHash(parent).WithMixHash(randao).TestObject;
            byte[] raw = Rlp.Encode(l1).Bytes;
            chain.Append([raw], new NativeL1HeaderRules(null, null, null, null, null));
            parent = new Hash256(chain.Endpoint.Hash);
        }
        EezChainRules rules = contractV3 ? ContractV3HistoryRules(chain) : NativeRules();
        WithProcessor(rules, (scope, state, _, header) =>
        {
            state.CreateAccount(EezConstants.L1HistoryAddress, 0, 1);
            byte[] runtime = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rollupx", contractV3 ? "native-offline-v3" : "native-profile-v1", "history-runtime.bin"));
            Assert.That(ValueKeccak.Compute(runtime), Is.EqualTo(NativeHistoryBlockExecutor.GetRuntimeHash(rules.NativeFormat)), "exact unchanged upstream runtime");
            state.InsertCode(EezConstants.L1HistoryAddress, runtime, Osaka.Instance);
            ulong[] slots = [99, 99, 120, 12, 6, 64];
            for (int index = 0; index < slots.Length; index++)
                state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)(index + 1)), slots[index]);
            state.Set(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(99)), chain.At(99).Hash.ToUInt256());
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether);
            state.CreateAccount(TestItem.AddressB, 0, 1);
            state.InsertCode(TestItem.AddressB, new byte[] { (byte)Instruction.PREVRANDAO, (byte)Instruction.PUSH0, (byte)Instruction.SSTORE, (byte)Instruction.STOP }, Osaka.Instance);
            state.Commit(Osaka.Instance);
            action(scope, state, header, chain);
        });
    }
}
