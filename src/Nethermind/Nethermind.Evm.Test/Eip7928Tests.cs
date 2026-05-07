// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-7928 Block Access Lists.
/// Verifies that executing EVM code correctly records state accesses into a
/// <see cref="BlockAccessListAtIndex"/> via <see cref="TracedAccessWorldState"/>.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
public class Eip7928Tests(bool parallel) : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static readonly EthereumEcdsa _ecdsa = new(0);
    private static readonly UInt256 _accountBalance = 10.Ether;
    private static readonly UInt256 _testAccountBalance = 1.Ether;
    private static readonly long _gasLimit = 150000;
    private static readonly Address _testAddress = ContractAddress.From(TestItem.AddressA, 0);
    private static readonly Address _callTargetAddress = TestItem.AddressC;
    private static readonly Address _delegationTargetAddress = TestItem.AddressD;
    private static readonly UInt256 _delegationSlot = 10;
    private static readonly byte[] _delegatedCode = Prepare.EvmCode
        .PushData(_delegationSlot)
        .Op(Instruction.SLOAD)
        .Done;

    /// <summary>
    /// Creates a fresh <see cref="TracedAccessWorldState"/> wrapping <see cref="VirtualMachineTestsBase.TestState"/>
    /// and a matching <see cref="TransactionProcessor{EthereumGasPolicy}"/> wired to it.
    /// </summary>
    private (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) CreateTracedProcessor(bool? parallelOverride = null)
    {
        bool useParallel = parallelOverride ?? parallel;
        TracedAccessWorldState tracedState = new(TestState, parallel: useParallel);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        ILogManager logManager = LimboLogs.Instance;
        IBlockhashProvider blockhashProvider = new TestBlockhashProvider(SpecProvider);
        EthereumCodeInfoRepository codeInfoRepo = new(tracedState);
        EthereumVirtualMachine machine = new(blockhashProvider, SpecProvider, logManager);
        TransactionProcessor<EthereumGasPolicy> processor = new(
            BlobBaseFeeCalculator.Instance, SpecProvider, tracedState, machine, codeInfoRepo, logManager, parallel: useParallel);
        return (tracedState, processor);
    }

    private static void AssertPureAccountRead(AccountChangesAtIndex? accountChanges)
    {
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.BalanceChange, Is.Null);
        Assert.That(accountChanges.NonceChange, Is.Null);
        Assert.That(accountChanges.CodeChange, Is.Null);
    }

    /// <summary>
    /// Asserts equality between an expected <see cref="ReadOnlyAccountChanges"/> (single-index
    /// expectations) and the produced <see cref="AccountChangesAtIndex"/>. The traced state
    /// always operates at index 0 in these tests, so each scalar change list has at most one
    /// entry.
    /// </summary>
    private static void AssertEqual(ReadOnlyAccountChanges expected, AccountChangesAtIndex? actual)
    {
        Assert.That(actual, Is.Not.Null);
        Assert.That(actual!.Address, Is.EqualTo(expected.Address));

        Assert.That(actual.BalanceChange, expected.BalanceChanges.Length == 0
            ? Is.Null
            : Is.EqualTo((BalanceChange?)expected.BalanceChanges[0]));
        Assert.That(actual.NonceChange, expected.NonceChanges.Length == 0
            ? Is.Null
            : Is.EqualTo((NonceChange?)expected.NonceChanges[0]));
        Assert.That(actual.CodeChange, expected.CodeChanges.Length == 0
            ? Is.Null
            : Is.EqualTo((CodeChange?)expected.CodeChanges[0]));

        // Compare storage changes (one entry per slot, all at index 0).
        Dictionary<UInt256, StorageChange> actualStorage = [];
        foreach (KeyValuePair<UInt256, StorageChange> kv in actual.StorageChanges)
        {
            actualStorage[kv.Key] = kv.Value;
        }
        Assert.That(actualStorage.Count, Is.EqualTo(expected.StorageChanges.Length));
        foreach (ReadOnlySlotChanges slot in expected.StorageChanges)
        {
            Assert.That(actualStorage.TryGetValue(slot.Key, out StorageChange actualChange), Is.True);
            // Expected slot has exactly one change.
            StorageChange expectedChange = slot.Changes[0];
            Assert.That(actualChange, Is.EqualTo(expectedChange));
        }

        Assert.That(actual.StorageReads, Is.EquivalentTo(expected.StorageReads));
    }

    [TestCaseSource(nameof(CodeTestSource))]
    public async Task Constructs_BAL_when_processing_code(
        IEnumerable<ReadOnlyAccountChanges> expected,
        byte[] code,
        byte[]? extraCode,
        bool revert)
    {
        InitWorldState(TestState, extraCode);

        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) = CreateTracedProcessor();

        UInt256 value = _testAccountBalance;
        Block block = Build.A.Block.TestObject;

        Transaction templateTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(0)
            .WithValue(value)
            .TestObject;
        long gasLimit = IntrinsicGasCalculator.Calculate(templateTx, Amsterdam.Instance, block.Header.GasLimit).MinimalGas + _gasLimit;

        Transaction createTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(gasLimit)
            .WithValue(value)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;

        processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
        CallOutputTracer callOutputTracer = new();
        TransactionResult res = processor.Execute(createTx, callOutputTracer);
        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        UInt256 gasUsed = new((ulong)callOutputTracer.GasSpent);

        UInt256 newBalance = _accountBalance - gasUsed;
        if (!revert)
        {
            newBalance -= value;
        }
        ReadOnlyAccountChanges accountChangesA = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges([new(0, newBalance)])
            .WithNonceChanges([new(0, 1)]).TestObject;
        ReadOnlyAccountChanges accountChangesZero = Build.An.AccountChanges.WithBalanceChanges([new(0, gasUsed)]).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted);
            AssertEqual(accountChangesA, bal.GetAccountChanges(TestItem.AddressA));
            AssertEqual(accountChangesZero, bal.GetAccountChanges(Address.Zero));
            Assert.That(bal.AccountCount, Is.EqualTo(expected.Count() + 2));
        }

        foreach (ReadOnlyAccountChanges expectedAccountChanges in expected)
        {
            AccountChangesAtIndex? actual = bal.GetAccountChanges(expectedAccountChanges.Address);
            AssertEqual(expectedAccountChanges, actual);
        }
    }

    [TestCaseSource(nameof(ExceptionTestSource))]
    public async Task Constructs_BAL_when_processing_code_exception(
        IEnumerable<ReadOnlyAccountChanges> expected,
        byte[] code,
        byte[]? extraCode,
        long executionGas,
        EvmExceptionType expectedException)
    {
        InitWorldState(TestState, extraCode);

        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) = CreateTracedProcessor();
        Block block = Build.A.Block.TestObject;

        Transaction templateTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(0)
            .WithValue(_testAccountBalance)
            .TestObject;
        long intrinsicGas = IntrinsicGasCalculator.Calculate(templateTx, Amsterdam.Instance, block.Header.GasLimit).MinimalGas;
        long gasLimit = intrinsicGas + executionGas;

        Transaction createTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(gasLimit)
            .WithValue(_testAccountBalance)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;

        processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
        CallOutputTracer callOutputTracer = new();
        TransactionResult res = processor.Execute(createTx, callOutputTracer);
        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        UInt256 gasUsed = new((ulong)callOutputTracer.GasSpent);

        ReadOnlyAccountChanges accountChangesA = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges([new(0, _accountBalance - gasUsed)])
            .WithNonceChanges([new(0, 1)]).TestObject;
        ReadOnlyAccountChanges accountChangesZero = Build.An.AccountChanges.WithBalanceChanges([new(0, gasUsed)]).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.EvmExceptionType, Is.EqualTo(expectedException));
            AssertEqual(accountChangesA, bal.GetAccountChanges(TestItem.AddressA));
            AssertEqual(accountChangesZero, bal.GetAccountChanges(Address.Zero));
            Assert.That(bal.AccountCount, Is.EqualTo(expected.Count() + 2));
        }

        foreach (ReadOnlyAccountChanges expectedAccountChanges in expected)
        {
            AccountChangesAtIndex? actual = bal.GetAccountChanges(expectedAccountChanges.Address);
            AssertEqual(expectedAccountChanges, actual);
        }
    }

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

    /// <summary>
    /// Regression for the perf optimization in <c>cea517aa20</c>: when an outer CALL into an
    /// EIP-7702-delegated EOA OOGs at the cold-access gas charge for the delegation target,
    /// the delegation target's address must NOT appear in the BAL — only the call target
    /// (the EOA itself). The optimization had moved <c>GetCachedCodeInfo</c> (which loads the
    /// delegation target's code via <c>GetCodeHash</c>, recording it as a BAL account-read)
    /// before the cold-access OOG check, so the target ended up recorded even when the CALL
    /// frame never executed. Mirrors EELS's
    /// <c>test_bal_call_7702_delegation_and_oog[…oog_after_target_access]</c> family.
    /// </summary>
    [Test]
    public void Call_into_7702_delegated_eoa_oog_at_delegation_cold_access_does_not_record_delegation_target()
    {
        InitWorldState(TestState);

        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) = CreateTracedProcessor();
        Block block = Build.A.Block.TestObject;

        byte[] code = Prepare.EvmCode
            .Call(_callTargetAddress, 20_000)
            .Done;

        Transaction templateTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(0)
            .TestObject;
        long intrinsicGas = IntrinsicGasCalculator.Calculate(templateTx, Amsterdam.Instance, block.Header.GasLimit).MinimalGas;
        // Enough gas to push CALL operands and reach the cold-access charge for the EOA, but
        // 1 gas short of the cold-access charge for its delegation target. CALL pushes 7 stack
        // operands (3 each of GasCostOf.VeryLow), pays GasCostOf.Call, then ConsumeAccountAccessGas
        // for codeSource (cold), then for delegated (cold) — we cap at codeSource cold + 1 short.
        long pushOperandsCost = 7 * GasCostOf.VeryLow;
        long executionGas = pushOperandsCost + GasCostOf.Call + GasCostOf.ColdAccountAccess + GasCostOf.WarmStateRead - 1;

        Transaction tx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(intrinsicGas + executionGas)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;

        processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
        CallOutputTracer tracer = new();
        TransactionResult res = processor.Execute(tx, tracer);
        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted);
            // The CALL target (the delegated EOA itself) was loaded to resolve delegation,
            // so it IS in the BAL.
            Assert.That(bal.GetAccountChanges(_callTargetAddress), Is.Not.Null,
                "EIP-7702 delegated EOA must be recorded as the CALL target");
            // The delegation target was NEVER fully loaded — the CALL OOG'd at the cold-access
            // gas charge before GetCachedCodeInfo could load its code. It must not appear.
            Assert.That(bal.GetAccountChanges(_delegationTargetAddress), Is.Null,
                "EIP-7702 delegation target must not be recorded when CALL OOGs before its code is loaded");
        }
    }

    [TestCase(120_000_000L, 30_000_000L, true, TestName = "EIP2935_system_call_records_storage_change_when_state_gas_affordable")]
    [TestCase(120_000_000L, 30_000L, false, TestName = "EIP2935_system_call_records_only_read_when_state_gas_not_affordable")]
    public void Eip2935_system_call_bal_respects_eip8037_state_gas(long blockGasLimit, long systemCallGasLimit, bool shouldStoreParentHash)
    {
        InitWorldState(TestState);

        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) = CreateTracedProcessor();
        Hash256 parentHash = Keccak.Compute("parent");
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(blockGasLimit)
            .WithBaseFee(1.GWei)
            .WithParentHash(parentHash)
            .TestObject;
        processor.SetBlockExecutionContext(new BlockExecutionContext(header, Amsterdam.Instance));

        SystemCall systemCall = new()
        {
            Data = parentHash.BytesToArray(),
            GasLimit = systemCallGasLimit,
            GasPrice = header.BaseFeePerGas,
            SenderAddress = Address.SystemUser,
            To = Eip2935Constants.BlockHashHistoryAddress,
            Value = UInt256.Zero,
        };
        systemCall.Hash = systemCall.CalculateHash();

        processor.Execute(systemCall, NullTxTracer.Instance);

        AccountChangesAtIndex? accountChanges = tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(Eip2935Constants.BlockHashHistoryAddress);
        Assert.That(accountChanges, Is.Not.Null);
        if (shouldStoreParentHash)
        {
            KeyValuePair<UInt256, StorageChange> storageEntry = accountChanges!.StorageChanges.Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(accountChanges.StorageChangeCount, Is.EqualTo(1));
                Assert.That(storageEntry.Key, Is.EqualTo(UInt256.Zero));
                Assert.That(storageEntry.Value.Index, Is.EqualTo(0));
                Assert.That(storageEntry.Value.Value, Is.EqualTo(new UInt256(parentHash.Bytes, isBigEndian: true)));
                Assert.That(accountChanges.StorageReads, Is.Empty);
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accountChanges!.StorageChangeCount, Is.EqualTo(0));
                Assert.That(accountChanges.StorageReads, Is.EquivalentTo(new[] { UInt256.Zero }));
            }
        }
    }

    private static IEnumerable<TestCaseData> CodeTestSource
    {
        get
        {
            IEnumerable<ReadOnlyAccountChanges> changes;
            UInt256 slot = _delegationSlot;
            byte[] code = Prepare.EvmCode
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Done;

            ReadOnlyAccountChanges readAccount = Build.An.AccountChanges
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
            ReadOnlyAccountChanges testAccount = Build.An.AccountChanges
                .WithAddress(_testAddress)
                .WithNonceChanges([new(0, 1)])
                .WithBalanceChanges([new(0, _testAccountBalance)])
                .TestObject;
            ReadOnlyAccountChanges emptyBAccount = new(TestItem.AddressB);
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
                .PushData(2)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Op(Instruction.POP)
                .Create(
                    Prepare.EvmCode
                        .ForInitOf(Prepare.EvmCode.Op(Instruction.STOP).Done)
                        .Done,
                    0)
                .Op(Instruction.POP)
                .CallWithValue(TestItem.AddressB, 20_000, 1)
                .Op(Instruction.POP)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.REVERT)
                .Done;
            // revert should convert storage load to read, nonce and balance changes revert
            changes =
            [
                Build.An.AccountChanges
                    .WithAddress(_testAddress)
                    .WithStorageReads(slot)
                    .TestObject
            ];
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
                new ReadOnlyAccountChanges(_delegationTargetAddress)
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

            byte[] returnValueCode = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.CALLVALUE)
                .Op(Instruction.CALLER)
                .PushData(20_000)
                .Op(Instruction.CALL)
                .Done;
            code = Prepare.EvmCode
                .CallWithValue(_callTargetAddress, 20_000, 1.GWei)
                .Done;
            changes = [testAccount, new(_callTargetAddress)];
            yield return new TestCaseData(changes, code, returnValueCode, false) { TestName = "balance_change_return_to_original" };

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
                new ReadOnlyAccountChanges(_callTargetAddress)
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
                new ReadOnlyAccountChanges(_callTargetAddress)
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

            code = Prepare.EvmCode
                .Call(TestItem.AddressB, 20_000)
                .Done;
            changes = [testAccount, new(TestItem.AddressB)];
            yield return new TestCaseData(changes, code, null, false) { TestName = "zero_value_call" };
        }
    }

    private static IEnumerable<TestCaseData> ExceptionTestSource
    {
        get
        {
            IEnumerable<ReadOnlyAccountChanges> changes;
            byte[] code;
            UInt256 slot = _delegationSlot;
            ReadOnlyAccountChanges testAccount = new(_testAddress);
            ReadOnlyAccountChanges addressB = new(TestItem.AddressB);
            ReadOnlyAccountChanges callTarget = new(_callTargetAddress);

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
                EvmExceptionType.OutOfGas)
            { TestName = "balance_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "extcodesize_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "extcodehash_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "extcodecopy_oog_pre_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "extcodecopy_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "call_oog_pre_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "call_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "callcode_oog_pre_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "callcode_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "delegatecall_oog_pre_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "delegatecall_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "staticcall_oog_pre_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "staticcall_oog_post_state_access" };

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
                GasCostOf.CreateRegular + GasCostOf.InitCodeWord + GasCostOf.Memory - 1,
                EvmExceptionType.OutOfGas)
            { TestName = "create_oog_pre_state_access" };

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
                GasCostOf.CreateRegular + GasCostOf.InitCodeWord + GasCostOf.Sha3Word + GasCostOf.Memory - 1,
                EvmExceptionType.OutOfGas)
            { TestName = "create2_oog_pre_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "sload_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "sstore_oog_pre_state_access" };

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
                GasCostOf.ColdSLoad + GasCostOf.SSetRegular - 1,
                EvmExceptionType.OutOfGas)
            { TestName = "sstore_oog_post_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "selfdestruct_oog_pre_state_access" };

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
                EvmExceptionType.OutOfGas)
            { TestName = "selfdestruct_oog_post_state_access" };

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

    [Test]
    [TestCase("0x0000000000000000000000000000000000000004", TestName = "Precompile")]
    [TestCase("0x5000001000000000000000000000000000000004", TestName = "RandomAddress")]
    public void CodeInfoRepository_getcachedcodeinfo_records_account_read_in_bal(string address)
    {
        TracedAccessWorldState tracedState = new(TestState, parallel: parallel);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());

        CodeInfoRepository repo = new(tracedState, new EthereumPrecompileProvider());

        repo.GetCachedCodeInfo(new(address), false, Amsterdam.Instance, out Address? delegationAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delegationAddress, Is.Null);
            AssertPureAccountRead(tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(new(address)));
        }
    }

    [Test]
    public void Tx_exceeding_block_gas_limit_rejected_in_parallel_mode()
    {
        (_, TransactionProcessor<EthereumGasPolicy> processor) = CreateTracedProcessor(parallelOverride: true);

        TestState.CreateAccount(TestItem.AddressA, 10.Ether);
        TestState.Commit(SpecProvider.GenesisSpec);

        long blockGasLimit = 100_000;
        BlockHeader header = Build.A.BlockHeader
            .WithGasLimit(blockGasLimit)
            .WithNumber(1)
            .TestObject;
        processor.SetBlockExecutionContext(new BlockExecutionContext(header, Amsterdam.Instance));

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit(blockGasLimit + 1)
            .WithGasPrice(1)
            .WithValue(0)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        TransactionResult result = processor.Execute(tx, NullTxTracer.Instance);

        Assert.That(result, Is.EqualTo(TransactionResult.BlockGasLimitExceeded));
    }

    [Test]
    public void CodeInfoRepository_getcachedcodeinfo_delegated_records_account_read_in_bal()
    {
        byte[] targetCode = [(byte)Instruction.STOP];
        Address delegationTarget = TestItem.AddressC;
        Address delegatedAccount = TestItem.AddressD;

        TestState.CreateAccount(delegationTarget, 0);
        TestState.InsertCode(delegationTarget, targetCode, SpecProvider.GenesisSpec);

        byte[] delegationCode = [.. Eip7702Constants.DelegationHeader, .. delegationTarget.Bytes];
        TestState.CreateAccount(delegatedAccount, 0);
        TestState.InsertCode(delegatedAccount, delegationCode, SpecProvider.GenesisSpec);

        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);

        TracedAccessWorldState tracedState = new(TestState, parallel: parallel);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());

        CodeInfoRepository repo = new(tracedState, new EthereumPrecompileProvider());
        CodeInfo result = repo.GetCachedCodeInfo(delegatedAccount, true, Amsterdam.Instance, out Address? delegationAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delegationAddress, Is.EqualTo(delegationTarget));
            Assert.That(result.CodeSpan.ToArray(), Is.EqualTo(targetCode));
            // Both the delegated account and the delegation target are traced as account reads in the BAL
            Assert.That(tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(delegatedAccount), Is.Not.Null);
            Assert.That(tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(delegationTarget), Is.Not.Null);
        }
    }

    [Test]
    public void CacheCodeInfoRepository_tracing_records_account_read_in_bal()
    {
        CacheCodeInfoRepository.Clear();

        byte[] code = [(byte)Instruction.STOP];

        // Set up state directly on TestState (the inner world state)
        TestState.CreateAccount(TestItem.AddressB, 0);
        TestState.InsertCode(TestItem.AddressB, code, SpecProvider.GenesisSpec);
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);

        TracedAccessWorldState tracedState = new(TestState, parallel: parallel);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());

        CacheCodeInfoRepository repo = new(tracedState, new EthereumPrecompileProvider());
        CodeInfo result = repo.GetCachedCodeInfo(TestItem.AddressB, false, Amsterdam.Instance, out Address? delegationAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CodeSpan.ToArray(), Is.EqualTo(code));
            Assert.That(delegationAddress, Is.Null);
            // GetCachedCodeInfo records a pure account read even through the cache layer
            AssertPureAccountRead(tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressB));
        }
    }

}
