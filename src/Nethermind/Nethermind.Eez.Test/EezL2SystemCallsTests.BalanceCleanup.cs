// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezL2SystemCallsTests
{
    [Test]
    public void Process_OrdinaryProtocolDonationsAndSpending_UsesSelectedCleanupBoundary([Values] bool blockEnd)
    {
        EezChainRules rules = EezChainRules.NativeRollupX(ContractV3HistoryRules(ApplicationHistoryChain()).Schedule!, TestItem.AddressD,
            blockEnd ? NativeSystemTxFormat.ContractV6 : NativeSystemTxFormat.ContractV4, 2);
        WithProcessor(rules, (scope, state, _, header) =>
        {
            Address manager = rules.Eezl2Address;
            Address recipient = Address.FromNumber(9876);
            byte[] probe = Prepare.EvmCode.PushData(Address.SystemUser).Op(Instruction.BALANCE).PushData(0).Op(Instruction.SSTORE)
                .PushData(manager).Op(Instruction.BALANCE).PushData(1).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
            byte[] spender = Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0).PushData(5)
                .PushData(recipient).Op(Instruction.GAS).Op(Instruction.CALL).Op(Instruction.POP).Op(Instruction.STOP).Done;
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether);
            state.CreateAccount(manager, 5, 2);
            state.InsertCode(manager, spender, Osaka.Instance);
            state.Set(new StorageCell(manager, UInt256.One), 17);
            state.CreateAccount(TestItem.AddressB, 0, 1);
            state.InsertCode(TestItem.AddressB, probe, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction[] transactions = [CleanupUser(0, Address.SystemUser, 7), CleanupUser(1, TestItem.AddressB, 0), CleanupUser(2, manager, 0)];
            Block block = new(header, transactions, []);
            using BlockReceiptsTracer tracer = new();
            tracer.StartNewBlockTrace(block);
            TxReceipt[] receipts = scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>().ProcessTransactions(block, ProcessingOptions.None, tracer);
            state.Get(new StorageCell(TestItem.AddressB, UInt256.Zero), out UInt256 systemSeen);
            state.Get(new StorageCell(TestItem.AddressB, UInt256.One), out UInt256 managerSeen);
            state.Get(new StorageCell(manager, UInt256.One), out UInt256 preserved);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(systemSeen, Is.EqualTo((UInt256)7), "ordinary donation remains visible within its block");
                Assert.That(managerSeen, Is.EqualTo(blockEnd ? (UInt256)5 : UInt256.Zero));
                Assert.That(state.GetBalance(recipient), Is.EqualTo(blockEnd ? (UInt256)5 : UInt256.Zero),
                    "only block-end cleanup permits spending the existing protocol balance during the block");
                Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo(blockEnd ? UInt256.Zero : (UInt256)7));
                Assert.That(state.GetBalance(manager), Is.Zero);
                Assert.That(state.GetNonce(manager), Is.EqualTo(2UL));
                Assert.That(state.GetCodeHash(manager), Is.EqualTo(ValueKeccak.Compute(spender)));
                Assert.That(preserved, Is.EqualTo((UInt256)17));
                Assert.That(state.GetBalance(rules.FeeCollector!), Is.GreaterThan(UInt256.Zero));
                Assert.That(header.GasUsed, Is.EqualTo(receipts[^1].GasUsedTotal));
                foreach (TxReceipt receipt in receipts)
                {
                    Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
                    Assert.That(receipt.Logs, Is.Empty);
                }
            }
        });
    }

    [Test]
    public void Process_EmptyNativeBlock_ClearsBalancesAfterGenesisOnly([Values(0UL, 1UL)] ulong number)
    {
        EezChainRules rules = EezChainRules.NativeRollupX(ContractV3HistoryRules(ApplicationHistoryChain()).Schedule!, TestItem.AddressD,
            NativeSystemTxFormat.ContractV6, 2);
        WithProcessor(rules, (scope, state, _, header) =>
        {
            state.CreateAccount(Address.SystemUser, 7);
            state.CreateAccount(rules.Eezl2Address, 5, 2);
            state.InsertCode(rules.Eezl2Address, new byte[] { 0x00 }, Osaka.Instance);
            state.CreateAccount(rules.FeeCollector!, 23);
            state.Commit(Osaka.Instance);
            header.Number = number;
            Block block = new(header, [], []);
            using BlockReceiptsTracer tracer = new();
            tracer.StartNewBlockTrace(block);
            TxReceipt[] receipts = scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>().ProcessTransactions(block, ProcessingOptions.None, tracer);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(receipts, Is.Empty);
                Assert.That(header.GasUsed, Is.Zero);
                Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo(number == 0 ? (UInt256)7 : UInt256.Zero));
                Assert.That(state.AccountExists(Address.SystemUser), Is.EqualTo(number == 0));
                Assert.That(state.GetBalance(rules.Eezl2Address), Is.EqualTo(number == 0 ? (UInt256)5 : UInt256.Zero));
                Assert.That(state.GetNonce(rules.Eezl2Address), Is.EqualTo(2UL));
                Assert.That(state.GetBalance(rules.FeeCollector!), Is.EqualTo((UInt256)23));
            }
        });
    }

    private static Transaction CleanupUser(ulong nonce, Address to, UInt256 value) => Build.A.Transaction.WithChainId(1)
        .WithNonce(nonce).WithTo(to).WithValue(value).WithGasLimit(200_000).WithGasPrice(2.GWei)
        .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
}
