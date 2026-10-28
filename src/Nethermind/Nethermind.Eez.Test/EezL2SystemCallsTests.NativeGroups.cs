// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
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
    public void Execute_ContractV3Group_ClearsResidueAfterSuccessfulOrRevertedUser([Values] bool revert, [Values] bool existingSystem, [Values] bool version6) =>
        WithProcessor(ContractV3Rules(version6 ? NativeSystemTxFormat.ContractV6 : NativeSystemTxFormat.ContractV3), (state, processor, header) =>
        {
            if (existingSystem) state.CreateAccount(Address.SystemUser, 17, 8);
            state.CreateAccount(EezConstants.RollupXEezl2Address, 5, 1);
            state.InsertCode(EezConstants.RollupXEezl2Address, StopCode, Osaka.Instance);
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether);
            state.CreateAccount(TestItem.AddressB, 0, 1);
            state.InsertCode(TestItem.AddressB, revert
                ? new byte[] { (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.REVERT }
                : StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction load = GroupTransaction(EezCalldata.LoadExecutionTableSelector, 7);
            Assert.That(processor.Execute(load, new CallOutputTracer()).TransactionExecuted, Is.True);
            Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo((UInt256)12), "load residue survives until the paired user completes");
            Transaction user = Build.A.Transaction.WithChainId(1).WithTo(TestItem.AddressB).WithGasLimit(100_000)
                .WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            CallOutputTracer tracer = new();
            Assert.That(processor.Execute(user, tracer).TransactionExecuted, Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.StatusCode, Is.EqualTo(revert ? StatusCode.Failure : StatusCode.Success));
                Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo(UInt256.Zero));
                Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo(UInt256.Zero));
                Assert.That(state.GetNonce(Address.SystemUser), Is.EqualTo(existingSystem ? 8UL : 0UL));
                Assert.That(state.GetNonce(TestItem.PrivateKeyA.Address), Is.EqualTo(1UL));
                Assert.That(state.GetBalance(TestItem.AddressD), Is.GreaterThan(UInt256.Zero), "ordinary base fees still reach the collector");
            }
        });

    [Test]
    public void Execute_ContractV3Inbound_CleansWithinItsOwnTransaction([Values] bool revert, [Values] bool existingSystem, [Values] bool version6) =>
        WithProcessor(ContractV3Rules(version6 ? NativeSystemTxFormat.ContractV6 : NativeSystemTxFormat.ContractV3), (state, processor, _) =>
        {
            if (existingSystem) state.CreateAccount(Address.SystemUser, 17, 8);
            state.CreateAccount(EezConstants.RollupXEezl2Address, 5, 1);
            state.InsertCode(EezConstants.RollupXEezl2Address, revert
                ? new byte[] { (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.REVERT }
                : StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            CallOutputTracer tracer = new();
            Assert.That(processor.Execute(GroupTransaction(EezCalldata.ExecuteIncomingCrossChainCallSelector, 7), tracer).TransactionExecuted, Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.StatusCode, Is.EqualTo(revert ? StatusCode.Failure : StatusCode.Success));
                Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo(UInt256.Zero));
                Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo(UInt256.Zero));
                Assert.That(state.GetNonce(Address.SystemUser), Is.EqualTo(existingSystem ? 8UL : 0UL));
            }
        });

    [Test]
    public void CallAndRestore_ContractV3Load_DoesNotPublishAGroupCursor() =>
        WithProcessor(ContractV3Rules(), (state, processor, _) =>
        {
            state.CreateAccount(EezConstants.RollupXEezl2Address, 5, 1);
            state.InsertCode(EezConstants.RollupXEezl2Address, StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction load = GroupTransaction(EezCalldata.LoadExecutionTableSelector, 7);
            Assert.That(processor.CallAndRestore(load, new CallOutputTracer()).TransactionExecuted, Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(state.AccountExists(Address.SystemUser), Is.False, "preview mint creation must roll back with the call");
                Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo((UInt256)5));
            }
            Assert.That(processor.Execute(load, new CallOutputTracer()).TransactionExecuted, Is.True,
                "discarding a preview must not make the actual load look like an interrupted group");
        });

    [Test]
    public void Execute_ContractV3RejectedLoad_DoesNotPublishAGroupCursor() =>
        WithProcessor(ContractV3Rules(), (state, processor, _) =>
        {
            state.CreateAccount(EezConstants.RollupXEezl2Address, 5, 1);
            state.InsertCode(EezConstants.RollupXEezl2Address, StopCode, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Transaction rejected = GroupTransaction(EezCalldata.LoadExecutionTableSelector, 0);
            rejected.GasLimit = 100;
            Assert.That(processor.Execute(rejected, new CallOutputTracer()).TransactionExecuted, Is.False);
            Assert.That(processor.Execute(GroupTransaction(EezCalldata.LoadExecutionTableSelector, 0), new CallOutputTracer()).TransactionExecuted, Is.True);
        });

    [Test]
    public void Execute_ContractV3RejectedUser_RetainsPendingGroupUntilAcceptedUser() =>
        WithProcessor(ContractV3Rules(), (state, processor, _) =>
        {
            state.CreateAccount(EezConstants.RollupXEezl2Address, 5, 1);
            state.InsertCode(EezConstants.RollupXEezl2Address, StopCode, Osaka.Instance);
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether);
            state.Commit(Osaka.Instance);
            Assert.That(processor.Execute(GroupTransaction(EezCalldata.LoadExecutionTableSelector, 7), new CallOutputTracer()).TransactionExecuted, Is.True);
            Transaction user = Build.A.Transaction.WithChainId(1).WithNonce(1).WithTo(TestItem.AddressB).WithGasLimit(100_000)
                .WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Assert.That(processor.Execute(user, new CallOutputTracer()).TransactionExecuted, Is.False);
            Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo((UInt256)12));
            user = Build.A.Transaction.WithChainId(1).WithNonce(0).WithTo(TestItem.AddressB).WithGasLimit(100_000)
                .WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Assert.That(processor.Execute(user, new CallOutputTracer()).TransactionExecuted, Is.True);
            Assert.That(state.GetBalance(EezConstants.RollupXEezl2Address), Is.EqualTo(UInt256.Zero),
                "a rejected user must not consume the load's pending cleanup");
        });

    private static EezChainRules ContractV3Rules(NativeSystemTxFormat format = NativeSystemTxFormat.ContractV3) =>
        EezChainRules.NativeRollupX(NativeSchedule(), TestItem.AddressD, format, 2);

    private static Transaction GroupTransaction(uint selector, UInt256 value)
    {
        byte[] input = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(input, selector);
        return NativeTransaction(value, EezChainRules.RollupXSystemTxGasCap, data: input);
    }
}
