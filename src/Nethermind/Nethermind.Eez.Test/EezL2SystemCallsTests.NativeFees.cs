// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezL2SystemCallsTests
{
    [TestCase(false, 0UL, false)]
    [TestCase(true, 30_000UL, false)]
    [TestCase(false, 30_000UL, true)]
    [TestCase(false, 21_000UL, true)]
    [TestCase(true, 42_000UL, true)]
    public void Execute_NativeCollectorCapacity_UsesExactFinalCredit(bool beneficiary, ulong headroom, bool accepted)
    {
        Address collector = beneficiary ? TestItem.AddressC : TestItem.AddressD;
        EezChainRules rules = EezChainRules.NativeRollupX(NativeSchedule(), collector);
        WithProcessor(rules, (state, processor, _) =>
        {
            UInt256 balance = UInt256.MaxValue - headroom * 1.GWei;
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether, 5);
            state.CreateAccount(TestItem.AddressB, 5, 1);
            state.CreateAccount(collector, balance);
            if (!beneficiary) state.CreateAccount(TestItem.AddressC, 13);
            state.InsertCode(TestItem.AddressB, StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction tx = Build.A.Transaction.WithChainId(1).WithNonce(5).WithTo(TestItem.AddressB).WithValue(0)
                .WithGasLimit(100_000).WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            TransactionResult result = default;
            if (accepted) result = processor.Execute(tx, new CallOutputTracer());
            else Assert.That(() => processor.Execute(tx, new CallOutputTracer()), Throws.TypeOf<InvalidBlockException>());
            UInt256 credit = 21_000UL * 1.GWei;
            using (Assert.EnterMultipleScope())
            {
                if (accepted) Assert.That(result.TransactionExecuted, Is.True, result.ToString());
                Assert.That(state.GetBalance(collector), Is.EqualTo(accepted ? balance + credit + (beneficiary ? credit : UInt256.Zero) : balance));
                if (!beneficiary) Assert.That(state.GetBalance(TestItem.AddressC), Is.EqualTo((UInt256)13 + (accepted ? credit : UInt256.Zero)));
            }
        });
    }

    [Test]
    public void Execute_NativeFreeTransaction_IgnoresUnusedCollectorCapacity() =>
        WithProcessor(NativeRules(), (state, processor, _) =>
        {
            state.CreateAccount(EezConstants.RollupXEezl2Address, 0, 1);
            state.CreateAccount(TestItem.AddressD, UInt256.MaxValue);
            state.InsertCode(EezConstants.RollupXEezl2Address, StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            TransactionResult result = processor.Execute(NativeTransaction(0, EezChainRules.RollupXSystemTxGasCap), new CallOutputTracer());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.True, result.ToString());
                Assert.That(state.GetBalance(TestItem.AddressD), Is.EqualTo(UInt256.MaxValue));
            }
        });

    [Test]
    public void Execute_CollectorCreatedAndDestroyed_UsesProfileFeeRule([Values] bool native)
    {
        Address collector = ContractAddress.From(TestItem.PrivateKeyA.Address, 5);
        using JsonDocument genesis = JsonSerializer.SerializeToDocument(new { config = new { feeCollector = collector.ToString() } });
        EezChainRules rules = native ? EezChainRules.NativeRollupX(NativeSchedule(), collector) : EezChainRules.FromGenesis(genesis.RootElement);
        WithProcessor(rules, (state, processor, header) =>
        {
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether, 5);
            state.CreateAccount(TestItem.AddressC, 13);
            state.CreateAccount(collector, 17);
            state.Commit(Osaka.Instance);
            byte[] initCode = [(byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH0, (byte)Instruction.SSTORE,
                (byte)Instruction.PUSH20, .. TestItem.AddressC.Bytes, (byte)Instruction.SELFDESTRUCT];
            Transaction tx = Build.A.Transaction.WithChainId(1).WithNonce(5).WithCode(initCode).WithValue(0)
                .WithGasLimit(1_000_000).WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            CallOutputTracer tracer = new();
            TransactionResult result = processor.Execute(tx, tracer);
            state.Get(new StorageCell(collector, UInt256.Zero), out UInt256 stored);
            ulong gas = header.GasUsed;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.True);
                Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
                Assert.That(gas, Is.GreaterThan(21_000UL));
                Assert.That(state.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(1.Ether - gas * 2.GWei));
                Assert.That(state.GetNonce(TestItem.PrivateKeyA.Address), Is.EqualTo(6UL));
                Assert.That(state.GetBalance(TestItem.AddressC), Is.EqualTo((UInt256)30 + gas * 1.GWei));
                Assert.That(state.AccountExists(collector), Is.EqualTo(native));
                Assert.That(state.GetBalance(collector), Is.EqualTo(native ? gas * 1.GWei : UInt256.Zero));
                Assert.That(state.GetNonce(collector), Is.Zero);
                Assert.That(state.GetCode(collector).Length, Is.Zero);
                Assert.That(stored, Is.EqualTo(UInt256.Zero));
            }
        });
    }
}
