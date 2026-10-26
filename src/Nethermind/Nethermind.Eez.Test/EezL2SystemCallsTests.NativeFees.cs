// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
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
