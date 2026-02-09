// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip7928Tests() : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static readonly EthereumEcdsa _ecdsa = new(0);
    private static readonly UInt256 _accountBalance = 10.Ether();
    private static readonly UInt256 _testAccountBalance = 1.Ether();
    private static readonly long _gasLimit = 100000;
    private static readonly Address _testAddress = ContractAddress.From(TestItem.AddressA, 0);
    private static readonly Address _callTargetAddress = TestItem.AddressC;
    private static readonly Address _delegationTargetAddress = TestItem.AddressD;
    private static readonly UInt256 _delegationSlot = 10;
    private static readonly byte[] _delegatedCode = Prepare.EvmCode
        .PushData(_delegationSlot)
        .Op(Instruction.SLOAD)
        .Done;

    [TestCaseSource(nameof(CodeTestSource))]
    public async Task Constructs_BAL_when_processing_code(
        IEnumerable<AccountChanges> expected,
        byte[] code,
        byte[]? extraCode,
        bool revert)
    {
        InitWorldState(TestState, extraCode);
        ParallelWorldState worldState = TestState as ParallelWorldState;
        worldState.TracingEnabled = true;

        UInt256 value = _testAccountBalance;

        Transaction createTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(_gasLimit)
            .WithValue(value)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.TestObject;

        _processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
        CallOutputTracer callOutputTracer = new();
        TransactionResult res = _processor.Execute(createTx, callOutputTracer);
        BlockAccessList bal = worldState.GeneratedBlockAccessList;
        UInt256 gasUsed = new((ulong)callOutputTracer.GasSpent);

        UInt256 newBalance = _accountBalance - gasUsed;
        if (!revert)
        {
            newBalance -= value;
        }
        AccountChanges accountChangesA = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges([new(0, newBalance)])
            .WithNonceChanges([new(0, 1)]).TestObject;
        AccountChanges accountChangesZero = Build.An.AccountChanges.WithBalanceChanges([new(0, gasUsed)]).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted);
            Assert.That(bal.GetAccountChanges(TestItem.AddressA), Is.EqualTo(accountChangesA));
            Assert.That(bal.GetAccountChanges(Address.Zero), Is.EqualTo(accountChangesZero));
            Assert.That(bal.AccountChanges.Count(), Is.EqualTo(expected.Count() + 2));
        }

        foreach(AccountChanges expectedAccountChanges in expected)
        {
            AccountChanges actual = bal.GetAccountChanges(expectedAccountChanges.Address);
            Console.WriteLine($"expected: {JsonSerializer.Serialize(expectedAccountChanges)}");
            Console.WriteLine($"actual: {JsonSerializer.Serialize(actual)}");
            Assert.That(actual, Is.EqualTo(expectedAccountChanges));
        }
    }

    [TestCaseSource(nameof(OogTestSource))]
    public async Task Constructs_BAL_when_processing_code_runs_out_of_gas(
        IEnumerable<AccountChanges> expected,
        byte[] code,
        byte[]? extraCode,
        long executionGas,
        EvmExceptionType expectedException)
    {
        InitWorldState(TestState, extraCode);
        ParallelWorldState worldState = TestState as ParallelWorldState;
        worldState.TracingEnabled = true;

        Transaction templateTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(0)
            .WithValue(_testAccountBalance)
            .TestObject;
        long intrinsicGas = IntrinsicGasCalculator.Calculate(templateTx, Amsterdam.Instance).MinimalGas;
        long gasLimit = intrinsicGas + executionGas;

        Transaction createTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(gasLimit)
            .WithValue(_testAccountBalance)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.TestObject;

        _processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
        CallOutputTracer callOutputTracer = new();
        TransactionResult res = _processor.Execute(createTx, callOutputTracer);
        BlockAccessList bal = worldState.GeneratedBlockAccessList;
        UInt256 gasUsed = new((ulong)callOutputTracer.GasSpent);

        AccountChanges accountChangesA = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges([new(0, _accountBalance - gasUsed)])
            .WithNonceChanges([new(0, 1)]).TestObject;
        AccountChanges accountChangesZero = Build.An.AccountChanges.WithBalanceChanges([new(0, gasUsed)]).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.EvmExceptionType, Is.EqualTo(expectedException));
            Assert.That(bal.GetAccountChanges(TestItem.AddressA), Is.EqualTo(accountChangesA));
            Assert.That(bal.GetAccountChanges(Address.Zero), Is.EqualTo(accountChangesZero));
            Assert.That(bal.AccountChanges.Count(), Is.EqualTo(expected.Count() + 2));
        }

        foreach(AccountChanges expectedAccountChanges in expected)
        {
            AccountChanges actual = bal.GetAccountChanges(expectedAccountChanges.Address);
            Console.WriteLine($"expected: {JsonSerializer.Serialize(expectedAccountChanges)}");
            Console.WriteLine($"actual: {JsonSerializer.Serialize(actual)}");
            Assert.That(actual, Is.EqualTo(expectedAccountChanges));
        }
    }

    private Action<ContainerBuilder> BuildContainer()
        => containerBuilder => containerBuilder.AddSingleton(SpecProvider);

    private void InitWorldState(IWorldState worldState, byte[]? extraCode = null)
    {
        worldState.CreateAccount(TestItem.AddressA, _accountBalance);

        worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, Eip2935TestConstants.Nonce);
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, Eip2935TestConstants.CodeHash, Eip2935TestConstants.Code, SpecProvider.GenesisSpec);

        worldState.CreateAccount(Eip4788Constants.BeaconRootsAddress, 0, Eip4788TestConstants.Nonce);
        worldState.InsertCode(Eip4788Constants.BeaconRootsAddress, Eip4788TestConstants.CodeHash, Eip4788TestConstants.Code, SpecProvider.GenesisSpec);

        worldState.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
        worldState.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, SpecProvider.GenesisSpec);

        worldState.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
        worldState.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, SpecProvider.GenesisSpec);

        worldState.CreateAccount(_delegationTargetAddress, 0);
        worldState.InsertCode(_delegationTargetAddress, ValueKeccak.Compute(_delegatedCode), _delegatedCode, SpecProvider.GenesisSpec);

        worldState.CreateAccount(_callTargetAddress, 0);
        if (extraCode is not null)
        {
            ValueHash256 codeHash = ValueKeccak.Compute(extraCode);
            worldState.InsertCode(_callTargetAddress, codeHash, extraCode, SpecProvider.GenesisSpec);
        }
        else
        {
            byte[] delegationCode = [.. Eip7702Constants.DelegationHeader, .. _delegationTargetAddress.Bytes];
            worldState.InsertCode(_callTargetAddress, ValueKeccak.Compute(delegationCode), delegationCode, SpecProvider.GenesisSpec);
        }

        worldState.Commit(SpecProvider.GenesisSpec);
        worldState.CommitTree(0);
        worldState.RecalculateStateRoot();
    }

    private static IEnumerable<TestCaseData> CodeTestSource
    {
        get
        {
            IEnumerable<AccountChanges> changes;
            UInt256 slot = _delegationSlot;
            byte[] code = Prepare.EvmCode
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;

            AccountChanges readAccount = Build.An.AccountChanges
                .WithAddress(_testAddress)
                .WithStorageReads(slot)
                .WithNonceChanges([new(0, 1)])
                .WithBalanceChanges([new(0, _testAccountBalance)])
                .TestObject;
            changes = [readAccount];
            yield return new TestCaseData(changes, code, null, false) { TestName = "storage_read" };

            code = Prepare.EvmCode
                .PushData(slot)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .Done;
            changes = [Build.An.AccountChanges
                .WithAddress(_testAddress)
                .WithStorageChanges(slot, [new(0, slot)])
                .WithNonceChanges([new(0, 1)])
                .WithBalanceChanges([new(0, _testAccountBalance)])
                .TestObject];
            yield return new TestCaseData(changes, code, null, false) { TestName = "storage_write" };

            code = Prepare.EvmCode
                .PushData(slot)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .Done;
            changes = [readAccount];
            yield return new TestCaseData(changes, code, null, false) { TestName = "storage_write_return_to_original" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.BALANCE)
                .Done;
            AccountChanges testAccount = Build.An.AccountChanges
                .WithAddress(_testAddress)
                .WithNonceChanges([new(0, 1)])
                .WithBalanceChanges([new(0, _testAccountBalance)])
                .TestObject;
            AccountChanges emptyBAccount = new(TestItem.AddressB);
            changes = [testAccount, emptyBAccount];
            yield return new TestCaseData(changes, code, null, false) { TestName = "balance" };

            code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODECOPY)
                .Done;
            changes = [testAccount, emptyBAccount];
            yield return new TestCaseData(changes, code, null, false) { TestName = "extcodecopy" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODEHASH)
                .Done;
            changes = [testAccount, emptyBAccount];
            yield return new TestCaseData(changes, code, null, false) { TestName = "extcodehash" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODESIZE)
                .Done;
            changes = [testAccount, emptyBAccount];
            yield return new TestCaseData(changes, code, null, false) { TestName = "extcodesize" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.SELFDESTRUCT)
                .Done;
            changes = [new(_testAddress), Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithBalanceChanges([new(0, _testAccountBalance)]).TestObject];
            yield return new TestCaseData(changes, code, null, false) { TestName = "selfdestruct" };

            code = Prepare.EvmCode
                .PushData(slot)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.REVERT)
                .Done;
            // revert should convert storage load to read, nonce and balance changes revert
            changes = [Build.An.AccountChanges
                .WithAddress(_testAddress)
                .WithStorageReads(slot)
                .TestObject];
            yield return new TestCaseData(changes, code, null, true) { TestName = "revert" };

            UInt256 changedValue = 2;
            byte[] revertToPreviousCode = Prepare.EvmCode
                .PushData(0)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.REVERT)
                .Done;
            code = Prepare.EvmCode
                .PushData(changedValue)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .DelegateCall(_callTargetAddress, 20_000)
                .Done;
            changes = [new(_callTargetAddress), Build.An.AccountChanges
                .WithAddress(_testAddress)
                .WithStorageChanges(slot, [new(0, changedValue)])
                .WithNonceChanges([new(0, 1)])
                .WithBalanceChanges([new(0, _testAccountBalance)])
                .TestObject];
            yield return new TestCaseData(changes, code, revertToPreviousCode, false)
                { TestName = "revert_with_return_to_original" };

            code = Prepare.EvmCode
                .Call(_callTargetAddress, 20_000)
                .Done;
            changes = [
                testAccount,
                Build.An.AccountChanges
                    .WithAddress(_callTargetAddress)
                    .WithStorageReads(_delegationSlot)
                    .TestObject,
                new AccountChanges(_delegationTargetAddress)
            ];
            yield return new TestCaseData(changes, code, null, false) { TestName = "delegated_account" };

            UInt256 callValue = 10_000;
            byte[] callTargetCode = Prepare.EvmCode
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            code = Prepare.EvmCode
                .CallWithValue(_callTargetAddress, 20_000, callValue)
                .Done;
            changes = [
                Build.An.AccountChanges
                .WithAddress(_testAddress)
                .WithNonceChanges([new(0, 1)])
                .WithBalanceChanges([new(0, _testAccountBalance - callValue)])
                .TestObject,
                Build.An.AccountChanges
                    .WithAddress(_callTargetAddress)
                    .WithStorageReads(slot)
                    .WithBalanceChanges([new(0, callValue)])
                    .TestObject
            ];
            yield return new TestCaseData(changes, code, callTargetCode, false) { TestName = "call" };

            code = Prepare.EvmCode
                .CallCode(_callTargetAddress, 20_000)
                .Done;
            changes = [
                Build.An.AccountChanges
                    .WithAddress(_testAddress)
                    .WithNonceChanges([new(0, 1)])
                    .WithBalanceChanges([new(0, _testAccountBalance)])
                    .WithStorageReads(slot)
                    .TestObject,
                new AccountChanges(_callTargetAddress)
            ];
            // storage read happens in test account context
            yield return new TestCaseData(changes, code, callTargetCode, false) { TestName = "callcode" };

            code = Prepare.EvmCode
                .DelegateCall(_callTargetAddress, 20_000)
                .Done;
            changes = [
                Build.An.AccountChanges
                    .WithAddress(_testAddress)
                    .WithNonceChanges([new(0, 1)])
                    .WithBalanceChanges([new(0, _testAccountBalance)])
                    .WithStorageReads(slot)
                    .TestObject,
                new AccountChanges(_callTargetAddress)
            ];
            // storage read happens in test account context
            yield return new TestCaseData(changes, code, callTargetCode, false) { TestName = "delegatecall" };

            code = Prepare.EvmCode
                .StaticCall(_callTargetAddress, 20_000)
                .Done;
            changes = [
                testAccount,
                Build.An.AccountChanges
                    .WithAddress(_callTargetAddress)
                    .WithStorageReads(slot)
                    .TestObject
            ];
            yield return new TestCaseData(changes, code, callTargetCode, false) { TestName = "staticcall" };

            byte[] createdRuntimeCode = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;
            byte[] createInitCode = Prepare.EvmCode
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .ForInitOf(createdRuntimeCode)
                .Done;
            Address createdAddress = ContractAddress.From(_testAddress, 1);
            code = Prepare.EvmCode
                .Create(createInitCode, 0)
                .Done;
            changes = [
                Build.An.AccountChanges
                    .WithAddress(_testAddress)
                    .WithNonceChanges([new(0, 2)])
                    .WithBalanceChanges([new(0, _testAccountBalance)])
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(createdAddress)
                    .WithNonceChanges([new(0, 1)])
                    .WithStorageReads(slot)
                    .WithCodeChanges([new(0, createdRuntimeCode)])
                    .TestObject
            ];
            yield return new TestCaseData(changes, code, null, false) { TestName = "create" };

            byte[] create2Salt = new byte[32];
            create2Salt[^1] = 1;
            Address createdAddress2 = ContractAddress.From(_testAddress, create2Salt, createInitCode);
            code = Prepare.EvmCode
                .Create2(createInitCode, create2Salt, 0)
                .Done;
            changes = [
                Build.An.AccountChanges
                    .WithAddress(_testAddress)
                    .WithNonceChanges([new(0, 2)])
                    .WithBalanceChanges([new(0, _testAccountBalance)])
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(createdAddress2)
                    .WithNonceChanges([new(0, 1)])
                    .WithStorageReads(slot)
                    .WithCodeChanges([new(0, createdRuntimeCode)])
                    .TestObject
            ];
            yield return new TestCaseData(changes, code, null, false) { TestName = "create2" };

            code = Prepare.EvmCode
                .CallWithInput(PrecompiledAddresses.Identity, 20_000, [1, 2, 3, 4])
                .Done;
            changes = [testAccount, new(PrecompiledAddresses.Identity)];
            yield return new TestCaseData(changes, code, null, false) { TestName = "precompile" };
        }
    }

    private static IEnumerable<TestCaseData> OogTestSource
    {
        get
        {
            IEnumerable<AccountChanges> changes;
            byte[] code;
            UInt256 slot = _delegationSlot;
            AccountChanges testAccount = new(_testAddress);
            AccountChanges addressB = new(TestItem.AddressB);
            AccountChanges callTarget = new(_callTargetAddress);

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.BALANCE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.ColdAccountAccess - 1, EvmExceptionType.OutOfGas)
                { TestName = "balance_oog_pre_state_access" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.BALANCE)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            changes = [testAccount, addressB];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.ColdAccountAccess + GasCostOf.ColdSLoad - 1,
                EvmExceptionType.OutOfGas) { TestName = "balance_oog_post_state_access" };

            code = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.SelfBalance, EvmExceptionType.OutOfGas)
                { TestName = "selfbalance_oog_post_state_access" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODESIZE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.ColdAccountAccess - 1, EvmExceptionType.OutOfGas)
                { TestName = "extcodesize_oog_pre_state_access" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODESIZE)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            changes = [testAccount, addressB];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.ColdAccountAccess + GasCostOf.ColdSLoad - 1,
                EvmExceptionType.OutOfGas) { TestName = "extcodesize_oog_post_state_access" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.ColdAccountAccess - 1, EvmExceptionType.OutOfGas)
                { TestName = "extcodehash_oog_pre_state_access" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODEHASH)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            changes = [testAccount, addressB];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.ColdAccountAccess + GasCostOf.ColdSLoad - 1,
                EvmExceptionType.OutOfGas) { TestName = "extcodehash_oog_post_state_access" };

            code = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODECOPY)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.Memory,
                EvmExceptionType.OutOfGas) { TestName = "extcodecopy_oog_pre_state_access" };

            code = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressB)
                .Op(Instruction.EXTCODECOPY)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            changes = [testAccount, addressB];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.ColdAccountAccess + GasCostOf.Memory * 2 + GasCostOf.ColdSLoad - 1,
                EvmExceptionType.OutOfGas) { TestName = "extcodecopy_oog_post_state_access" };

            byte[] callTargetCode = Prepare.EvmCode.Op(Instruction.STOP).Done;
            code = Prepare.EvmCode
                .Call(_callTargetAddress, 0)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                callTargetCode,
                GasCostOf.ColdAccountAccess - 1,
                EvmExceptionType.OutOfGas) { TestName = "call_oog_pre_state_access" };

            code = Prepare.EvmCode
                .Call(_callTargetAddress, 0)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            changes = [testAccount, callTarget];
            yield return new TestCaseData(
                changes,
                code,
                callTargetCode,
                GasCostOf.ColdAccountAccess + GasCostOf.ColdSLoad - 1,
                EvmExceptionType.OutOfGas) { TestName = "call_oog_post_state_access" };

            code = Prepare.EvmCode
                .CallCode(_callTargetAddress, 0)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                callTargetCode,
                GasCostOf.ColdAccountAccess - 1,
                EvmExceptionType.OutOfGas) { TestName = "callcode_oog_pre_state_access" };

            code = Prepare.EvmCode
                .CallCode(_callTargetAddress, 0)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            changes = [testAccount, callTarget];
            yield return new TestCaseData(
                changes,
                code,
                callTargetCode,
                GasCostOf.ColdAccountAccess + GasCostOf.ColdSLoad - 1,
                EvmExceptionType.OutOfGas) { TestName = "callcode_oog_post_state_access" };

            code = Prepare.EvmCode
                .DelegateCall(_callTargetAddress, 0)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                callTargetCode,
                GasCostOf.ColdAccountAccess - 1,
                EvmExceptionType.OutOfGas) { TestName = "delegatecall_oog_pre_state_access" };

            code = Prepare.EvmCode
                .DelegateCall(_callTargetAddress, 0)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            changes = [testAccount, callTarget];
            yield return new TestCaseData(
                changes,
                code,
                callTargetCode,
                GasCostOf.ColdAccountAccess + GasCostOf.ColdSLoad - 1,
                EvmExceptionType.OutOfGas) { TestName = "delegatecall_oog_post_state_access" };

            code = Prepare.EvmCode
                .StaticCall(_callTargetAddress, 0)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                callTargetCode,
                GasCostOf.ColdAccountAccess - 1,
                EvmExceptionType.OutOfGas) { TestName = "staticcall_oog_pre_state_access" };

            code = Prepare.EvmCode
                .StaticCall(_callTargetAddress, 0)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;
            changes = [testAccount, callTarget];
            yield return new TestCaseData(
                changes,
                code,
                callTargetCode,
                GasCostOf.ColdAccountAccess + GasCostOf.ColdSLoad - 1,
                EvmExceptionType.OutOfGas) { TestName = "staticcall_oog_post_state_access" };

            code = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.CREATE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.Create + GasCostOf.InitCodeWord + GasCostOf.Memory - 1,
                EvmExceptionType.OutOfGas) { TestName = "create_oog_pre_state_access" };

            byte[] create2Salt = new byte[32];
            create2Salt[^1] = 1;
            code = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .PushData(create2Salt)
                .Op(Instruction.CREATE2)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.Create + GasCostOf.InitCodeWord + GasCostOf.Sha3Word + GasCostOf.Memory - 1,
                EvmExceptionType.OutOfGas) { TestName = "create2_oog_pre_state_access" };

            code = Prepare.EvmCode
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.ColdSLoad - 1, EvmExceptionType.OutOfGas)
                { TestName = "sload_oog_pre_state_access" };

            code = Prepare.EvmCode
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [Build.An.AccountChanges.WithAddress(_testAddress).WithStorageReads(slot).TestObject];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.ColdSLoad + GasCostOf.VeryLow + GasCostOf.Memory - 1,
                EvmExceptionType.OutOfGas) { TestName = "sload_oog_post_state_access" };

            code = Prepare.EvmCode
                .PushData(6)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.CallStipend - 1,
                EvmExceptionType.OutOfGas) { TestName = "sstore_oog_pre_state_access" };

            code = Prepare.EvmCode
                .PushData(6)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [Build.An.AccountChanges.WithAddress(_testAddress).WithStorageReads(slot).TestObject];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.ColdSLoad + GasCostOf.SSet - 1,
                EvmExceptionType.OutOfGas) { TestName = "sstore_oog_post_state_access" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.SELFDESTRUCT)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.SelfDestructEip150 + GasCostOf.ColdAccountAccess + GasCostOf.VeryLow - 1,
                EvmExceptionType.OutOfGas) { TestName = "selfdestruct_oog_pre_state_access" };

            code = Prepare.EvmCode
                .PushData(TestItem.AddressB)
                .Op(Instruction.SELFDESTRUCT)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .Done;
            changes = [testAccount, addressB];
            yield return new TestCaseData(
                changes,
                code,
                null,
                GasCostOf.SelfDestructEip150 + GasCostOf.ColdAccountAccess + GasCostOf.VeryLow,
                EvmExceptionType.OutOfGas) { TestName = "selfdestruct_oog_post_state_access" };

            code = Prepare.EvmCode.Op(Instruction.BALANCE).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "balance_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.EXTCODESIZE).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "extcodesize_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.EXTCODEHASH).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "extcodehash_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.EXTCODECOPY).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "extcodecopy_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.SLOAD).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "sload_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.SSTORE).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.CallStipend + 1, EvmExceptionType.StackUnderflow)
                { TestName = "sstore_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.CALL).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "call_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.CALLCODE).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "callcode_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.DELEGATECALL).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "delegatecall_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.STATICCALL).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "staticcall_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.CREATE).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "create_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.CREATE2).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.VeryLow, EvmExceptionType.StackUnderflow)
                { TestName = "create2_stack_underflow" };

            code = Prepare.EvmCode.Op(Instruction.SELFDESTRUCT).Done;
            changes = [testAccount];
            yield return new TestCaseData(changes, code, null, GasCostOf.SelfDestructEip150, EvmExceptionType.StackUnderflow)
                { TestName = "selfdestruct_stack_underflow" };
        }
    }
}
