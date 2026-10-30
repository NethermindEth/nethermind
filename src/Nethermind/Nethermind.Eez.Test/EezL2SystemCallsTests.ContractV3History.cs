// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezL2SystemCallsTests
{
    [Test]
    public void Process_ContractV3History_OptionalImportPreservesFixedCutoff([Values] bool import) =>
        WithHistory((scope, state, header, chain) =>
        {
            Block block = ContractV3HistoryBlock(header, chain, import);
            TxReceipt[] receipts = ExecuteHistory(ContractV3HistoryExecutor(scope, state, chain), block);
            state.Get(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)3), out UInt256 cutoff);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cutoff, Is.EqualTo((UInt256)120), "slot three remains the genesis cutoff even after an import");
                Assert.That(receipts.Length, Is.EqualTo(import ? 2 : 1));
                Assert.That(ReadStorage(state, TestItem.AddressB), Is.EqualTo(chain.At(import ? 100UL : 99UL).PrevRandao.ToUInt256()));
                Assert.That(block.GasUsed, Is.GreaterThan(0UL));
                if (import)
                {
                    Assert.That(block.Transactions[0].Data.IsEmpty, Is.True, "execution must not change the public wire input");
                    Assert.That(block.Transactions[0].GasLimit, Is.EqualTo(EezConstants.L1HistoryTxGasCap));
                    Assert.That(block.Transactions[0].SpentGas, Is.GreaterThan(21_000UL));
                }
            }
        }, contractV3: true);

    [Test]
    public void Process_ContractV3History_RejectsForgedHashBeforeApplicationExecution([Values] bool laterHash) =>
        WithHistory((scope, state, header, chain) =>
        {
            Block block = ContractV3HistoryBlock(header, chain, true);
            ValueHash256[] hashes = [chain.At(100).Hash, chain.At(101).Hash];
            hashes[laterHash ? 1 : 0] = TestItem.KeccakD.ValueHash256;
            block.Transactions[0] = new NativeHistoryTransaction(NativeHistoryEnvelope.Create(1, hashes));
            header.MixHash = new Hash256(chain.At(101).PrevRandao);
            AssertHistoryRejected(ContractV3HistoryExecutor(scope, state, chain), block, state);
        }, contractV3: true);

    [Test]
    public void Process_ContractV3Groups_RejectsIncompleteOrInterruptedLoadBeforeExecution(
        [Values("unfinished", "native", "history")] string interruption) =>
        WithHistory((scope, state, header, chain) =>
        {
            header.Timestamp = 126;
            header.MixHash = new Hash256(chain.At(99).PrevRandao);
            Transaction load = GroupTransaction(EezCalldata.LoadExecutionTableSelector, 0);
            Transaction[] transactions = interruption switch
            {
                "native" => [load, GroupTransaction(EezCalldata.ExecuteIncomingCrossChainCallSelector, 0)],
                "history" => [load, new NativeHistoryTransaction(NativeHistoryEnvelope.Create(1, [chain.At(100).Hash]))],
                _ => [load],
            };
            AssertHistoryRejected(ContractV3HistoryExecutor(scope, state, chain), new Block(header, transactions, []), state);
        }, contractV3: true);

    private static EezChainRules ContractV3HistoryRules(NativeL1HeaderChain chain) => EezChainRules.NativeRollupX(
        new RollupXSchedule(2, 6, new RollupXL1Reference(1, 99, new Hash256(chain.At(99).Hash), 108, new Hash256(chain.At(99).PrevRandao)), 120),
        TestItem.AddressD, NativeSystemTxFormat.ContractV3, 2);

    private static NativeHistoryBlockExecutor ContractV3HistoryExecutor(ILifetimeScope scope, IWorldState state, NativeL1HeaderChain chain) =>
        new(scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>(), state, scope.Resolve<ISpecProvider>(), ContractV3HistoryRules(chain),
            HistorySchedule, 99, HistorySource, chain);

    private static Block ContractV3HistoryBlock(BlockHeader header, NativeL1HeaderChain chain, bool import)
    {
        header.Timestamp = 126;
        header.ParentHash = TestItem.KeccakD;
        header.MixHash = new Hash256(chain.At(import ? 100UL : 99UL).PrevRandao);
        Transaction app = Build.A.Transaction.WithChainId(1).WithTo(TestItem.AddressB).WithValue(0).WithGasLimit(100_000)
            .WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        return new Block(header, import
            ? [new NativeHistoryTransaction(NativeHistoryEnvelope.Create(1, [chain.At(100).Hash])), app]
            : [app], []);
    }
}
