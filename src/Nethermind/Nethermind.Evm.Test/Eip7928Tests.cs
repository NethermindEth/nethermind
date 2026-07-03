// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
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
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static readonly EthereumEcdsa _ecdsa = new(0);
    private static readonly UInt256 _accountBalance = 10.Ether;
    private static readonly UInt256 _testAccountBalance = 1.Ether;
    private static readonly ulong _gasLimit = 150000;
    private static readonly Address _testAddress = ContractAddress.From(TestItem.AddressA, 0);
    private static readonly Address _callTargetAddress = TestItem.AddressC;
    private static readonly Address _delegationTargetAddress = TestItem.AddressD;
    private static readonly UInt256 _delegationSlot = 10;
    private static readonly byte[] _delegatedCode = Prepare.EvmCode
        .PushData(_delegationSlot)
        .Op(Instruction.SLOAD)
        .Done;

    private static IEnumerable<TestCaseData> SelfdestructSendToSenderTestSource()
    {
        yield return new TestCaseData(Shanghai.Instance, 0)
            .SetName("EIP7928_pre_EIP6780_selfdestruct_to_sender_zero_balance");
        yield return new TestCaseData(Shanghai.Instance, 100)
            .SetName("EIP7928_pre_EIP6780_selfdestruct_to_sender_nonzero_balance");
        yield return new TestCaseData(Amsterdam.Instance, 0)
            .SetName("EIP7928_EIP6780_selfdestruct_to_sender_zero_balance");
        yield return new TestCaseData(Amsterdam.Instance, 100)
            .SetName("EIP7928_EIP6780_selfdestruct_to_sender_nonzero_balance");
    }

    private (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) CreateTracedProcessor(
        bool? parallelOverride = null,
        bool wrapPrecompileCache = false)
    {
        bool useParallel = parallelOverride ?? parallel;
        TracedAccessWorldState tracedState = new(TestState, parallel: useParallel);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        ILogManager logManager = LimboLogs.Instance;
        IBlockhashProvider blockhashProvider = new TestBlockhashProvider(SpecProvider);
        EthereumCodeInfoRepository baseRepo = new(tracedState);
        ICodeInfoRepository codeInfoRepo = wrapPrecompileCache
            ? new PrecompileCachedCodeInfoRepository(tracedState, new EthereumPrecompileProvider(), baseRepo, precompileCache: null)
            : baseRepo;
        EthereumVirtualMachine machine = new(blockhashProvider, SpecProvider, logManager);
        TransactionProcessor<EthereumGasPolicy> processor = new(
            BlobBaseFeeCalculator.Instance, SpecProvider, tracedState, machine, codeInfoRepo, logManager, parallel: useParallel);
        return (tracedState, processor);
    }

    private static Transaction BuildContractTx(byte[] code, ulong executionGas, UInt256 value, BlockHeader header)
    {
        Transaction templateTx = Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(0)
            .WithValue(value)
            .TestObject;
        ulong intrinsicGas = IntrinsicGasCalculator.Calculate(templateTx, Amsterdam.Instance, header.GasLimit).MinimalGas;

        return Build.A.Transaction
            .WithCode(code)
            .WithGasLimit(intrinsicGas + executionGas)
            .WithValue(value)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
    }

    private (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor, Block block) SetupPrecompileBalScenario(
        Address? delegationTarget = null)
    {
        InitWorldState(TestState, delegationTarget: delegationTarget);
        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) =
            CreateTracedProcessor(wrapPrecompileCache: true);
        Block block = Build.A.Block.TestObject;
        processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
        return (tracedState, processor, block);
    }

    private static void AssertPureAccountRead(AccountChangesAtIndex? accountChanges)
    {
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.BalanceChange, Is.Null);
        Assert.That(accountChanges.NonceChange, Is.Null);
        Assert.That(accountChanges.CodeChange, Is.Null);
    }

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
            StorageChange expectedChange = slot.Changes[0];
            Assert.That(actualChange, Is.EqualTo(expectedChange));
        }

        Assert.That(actual.StorageReads, Is.EquivalentTo(expected.StorageReads));
    }

    private Transaction BuildSetCodeCallTx(Address to, params AuthorizationTuple[] authorizationList)
    {
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);

        return Build.A.Transaction
            .WithType(TxType.SetCode)
            .To(to)
            .WithGasLimit(1_000_000)
            .WithMaxFeePerGas(1)
            .WithMaxPriorityFeePerGas(1)
            .WithValue(UInt256.Zero)
            .WithAuthorizationCode(authorizationList)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA)
            .TestObject;
    }

    private void AddAccountToState(Address address, UInt256 nonce = default, byte[]? code = null, UInt256 balance = default)
    {
        TestState.CreateAccount(address, balance, (ulong)nonce);
        if (code is not null)
        {
            TestState.InsertCode(address, ValueKeccak.Compute(code), code, SpecProvider.GenesisSpec);
        }

        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);
        TestState.RecalculateStateRoot();
    }

    private static void AssertStorageChange(BlockAccessListAtIndex bal, Address address, UInt256 key, UInt256 value)
    {
        AccountChangesAtIndex? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.TryGetStorageChange(key, out StorageChange? slotChange), Is.True);
        Assert.That(slotChange!.Value.Value, Is.EqualTo(value.ToBigEndianWord()));
    }

    private static void AssertNonceChange(BlockAccessListAtIndex bal, Address address, ulong value)
    {
        AccountChangesAtIndex? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.NonceChange, Is.EqualTo((NonceChange?)new NonceChange(0, value)));
    }

    private static void AssertCodeChange(BlockAccessListAtIndex bal, Address address, byte[] code)
    {
        AccountChangesAtIndex? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.CodeChange, Is.Not.Null);
        Assert.That(accountChanges.CodeChange!.Value.Index, Is.EqualTo(0u));
        Assert.That(accountChanges.CodeChange.Value.Code, Is.EqualTo(code));
    }

    private static void AssertStorageRead(BlockAccessListAtIndex bal, Address address, UInt256 key)
    {
        AccountChangesAtIndex? accountChanges = bal.GetAccountChanges(address);
        Assert.That(accountChanges, Is.Not.Null);
        Assert.That(accountChanges!.StorageChangeCount, Is.EqualTo(0));
        Assert.That(accountChanges.StorageReads, Does.Contain(key));
    }

    private static byte[] BuildDelegationCode(Address target) =>
        [.. Eip7702Constants.DelegationHeader, .. target.Bytes];

    private static byte[] BuildStorageWriteCode(UInt256 key, UInt256 value) =>
        Prepare.EvmCode
            .PushData(value)
            .PushData(key)
            .Op(Instruction.SSTORE)
            .Done;

    private static byte[] BuildCallResultStorageWriteCode(Address target, UInt256 slot) =>
        Prepare.EvmCode
            .Call(target, 50_000)
            .PushData(slot)
            .Op(Instruction.SSTORE)
            .Done;

    private static byte[] BuildCallWithValueResultStorageWriteCode(Address target, UInt256 slot, UInt256 value) =>
        Prepare.EvmCode
            .CallWithValue(target, 50_000, value)
            .PushData(slot)
            .Op(Instruction.SSTORE)
            .Done;

    private static byte[] BuildCallOpcodeResultStorageWriteCode(Instruction callOpcode, Address target, UInt256 slot) =>
        callOpcode switch
        {
            Instruction.CALL => Prepare.EvmCode.Call(target, 50_000).PushData(slot).Op(Instruction.SSTORE).Done,
            Instruction.STATICCALL => Prepare.EvmCode.StaticCall(target, 50_000).PushData(slot).Op(Instruction.SSTORE).Done,
            Instruction.DELEGATECALL => Prepare.EvmCode.DelegateCall(target, 50_000).PushData(slot).Op(Instruction.SSTORE).Done,
            Instruction.CALLCODE => Prepare.EvmCode.CallCode(target, 50_000).PushData(slot).Op(Instruction.SSTORE).Done,
            _ => throw new ArgumentOutOfRangeException(nameof(callOpcode), callOpcode, null)
        };

    private static byte[] BuildCreateThenPopCode(Instruction createOpcode, byte[] initCode, byte[] salt, UInt256 value) =>
        (createOpcode == Instruction.CREATE2
            ? Prepare.EvmCode.Create2(initCode, salt, value)
            : Prepare.EvmCode.Create(initCode, value))
        .Op(Instruction.POP)
        .Op(Instruction.STOP)
        .Done;

    private static byte[] BuildCreate2ThenSetFlagCode(byte[] initCode)
    {
        byte[] returnFlagCode = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.SLOAD)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;
        int createJumpDestination = 5 + returnFlagCode.Length;
        byte[] prefix = Prepare.EvmCode
            .Op(Instruction.CALLDATASIZE)
            .Op(Instruction.ISZERO)
            .PushData(createJumpDestination)
            .Op(Instruction.JUMPI)
            .Done;
        byte[] loadFlagCode = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.SLOAD)
            .Done;
        byte[] createAndSetFlagCode = Prepare.EvmCode
            .ForCreate2Of(initCode)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        int createOnlyJumpDestination =
            createJumpDestination + 1 + loadFlagCode.Length + 3 + createAndSetFlagCode.Length;
        byte[] branchCode = Prepare.EvmCode
            .PushData(createOnlyJumpDestination)
            .Op(Instruction.JUMPI)
            .Done;
        byte[] createOnlyCode = Prepare.EvmCode
            .ForCreate2Of(initCode)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        return
        [
            .. prefix,
            .. returnFlagCode,
            (byte)Instruction.JUMPDEST,
            .. loadFlagCode,
            .. branchCode,
            .. createAndSetFlagCode,
            (byte)Instruction.JUMPDEST,
            .. createOnlyCode
        ];
    }

    private static byte[] BuildFlagConditionalSelfdestructInitCode(Address beneficiary, byte[] runtimeCode)
    {
        byte[] loadFlagCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .PushData(32)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.CALLER)
            .PushData(50_000)
            .Op(Instruction.CALL)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.MLOAD)
            .Done;
        byte[] selfdestructCode = Prepare.EvmCode
            .PushData(beneficiary)
            .Op(Instruction.SELFDESTRUCT)
            .Done;
        int returnJumpDestination = loadFlagCode.Length + 3 + selfdestructCode.Length;
        byte[] jumpCode = Prepare.EvmCode
            .PushData(returnJumpDestination)
            .Op(Instruction.JUMPI)
            .Done;
        byte[] returnCode = Prepare.EvmCode
            .ForInitOf(runtimeCode)
            .Done;

        return [.. loadFlagCode, .. jumpCode, .. selfdestructCode, (byte)Instruction.JUMPDEST, .. returnCode];
    }

    private AuthorizationTuple SignAuthorization(PrivateKey signer, Address codeAddress, ulong nonce = 0)
    {
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        return ecdsa.Sign(signer, SpecProvider.ChainId, codeAddress, nonce);
    }

    private static Transaction BuildCallTx(Address to, UInt256 value = default, UInt256 nonce = default) =>
        Build.A.Transaction
            .To(to)
            .WithNonce((ulong)nonce)
            .WithGasLimit(1_000_000)
            .WithGasPrice(1)
            .WithValue(value)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA)
            .TestObject;

    private BlockAccessListAtIndex ExecuteSetCodeCall(params AuthorizationTuple[] authorizationList) =>
        ExecuteSetCodeCall(wrapPrecompileCache: false, authorizationList);

    private BlockAccessListAtIndex ExecuteSetCodeCall(bool wrapPrecompileCache, params AuthorizationTuple[] authorizationList)
    {
        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) =
            CreateTracedProcessor(wrapPrecompileCache: wrapPrecompileCache);
        BlockHeader header = Build.A.BlockHeader
            .WithGasLimit(120_000_000)
            .WithBaseFee(1)
            .TestObject;
        Transaction tx = BuildSetCodeCallTx(_callTargetAddress, authorizationList);

        processor.SetBlockExecutionContext(new BlockExecutionContext(header, Amsterdam.Instance));
        TransactionResult res = processor.Execute(tx, NullTxTracer.Instance);

        Assert.That(res.TransactionExecuted, Is.True, res.ToString());
        return tracedState.GetGeneratingBlockAccessList()!;
    }

    private BlockAccessListAtIndex ExecuteCallTx(Address to) =>
        ExecuteCallTxs(BuildCallTx(to));

    private BlockAccessListAtIndex ExecuteCallTxs(params Transaction[] transactions)
    {
        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) = CreateTracedProcessor();
        BlockHeader header = Build.A.BlockHeader
            .WithGasLimit(120_000_000)
            .WithBaseFee(1)
            .TestObject;

        processor.SetBlockExecutionContext(new BlockExecutionContext(header, Amsterdam.Instance));
        for (int i = 0; i < transactions.Length; i++)
        {
            TransactionResult res = processor.Execute(transactions[i], NullTxTracer.Instance);
            Assert.That(res.TransactionExecuted, Is.True, res.ToString());

            if (i != transactions.Length - 1)
            {
                tracedState.IncrementIndex();
            }
        }

        return tracedState.GetGeneratingBlockAccessList()!;
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
        ulong gasLimit = IntrinsicGasCalculator.Calculate(templateTx, Amsterdam.Instance, block.Header.GasLimit).MinimalGas + _gasLimit;

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
        // With EIP-8037's higher CostPerStateByte, some CREATE/SELFDESTRUCT cases now run out
        // of state gas before the value transfer commits — the value stays on the sender.
        // Drive the expectation off the test case's own data: if the expected BAL records a
        // balance change for the created/test contract, the transfer succeeded.
        bool valuePersists = !revert && expected.Any(static accountChanges =>
            accountChanges.Address == _testAddress && accountChanges.BalanceChanges.Length > 0);
        if (valuePersists)
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
        ulong executionGas,
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
        ulong intrinsicGas = IntrinsicGasCalculator.Calculate(templateTx, Amsterdam.Instance, block.Header.GasLimit).MinimalGas;
        ulong gasLimit = intrinsicGas + executionGas;

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

    [Test]
    public void Delegated_precompile_target_is_recorded_in_BAL_under_PrecompileCachedCodeInfoRepository()
    {
        Address precompileAddress = Sha256Precompile.Address;
        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor, Block block) =
            SetupPrecompileBalScenario(delegationTarget: precompileAddress);

        byte[] code = Prepare.EvmCode.Call(_callTargetAddress, 50_000).Done;
        Transaction tx = BuildContractTx(code, _gasLimit, _testAccountBalance, block.Header);

        TransactionResult res = processor.Execute(tx, NullTxTracer.Instance);

        AccountChangesAtIndex? precompileChanges = tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(precompileAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted, Is.True);
            Assert.That(precompileChanges, Is.Not.Null,
                "delegated precompile target must be recorded in the BAL even when the PrecompileCachedCodeInfoRepository fast-path is active");
        }
    }

    /// <summary>
    /// EIP-7702: delegation to a precompile must NOT execute the precompile (FastCall returns 1).
    /// </summary>
    /// <remarks>
    /// Inner gas is 0 to discriminate: precompile execution OOGs and pushes 0, FastCall pushes 1 regardless of forwarded gas.
    /// </remarks>
    [Test]
    public void Calling_account_delegated_to_precompile_uses_FastCall_per_EIP_7702()
    {
        Address precompileAddress = Sha256Precompile.Address;
        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor, Block block) =
            SetupPrecompileBalScenario(delegationTarget: precompileAddress);

        byte[] code = Prepare.EvmCode
            .Call(_callTargetAddress, 0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;
        Transaction tx = BuildContractTx(code, _gasLimit, _testAccountBalance, block.Header);

        TransactionResult res = processor.Execute(tx, NullTxTracer.Instance);

        AccountChangesAtIndex? testAddressChanges = tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(_testAddress);
        StorageChange? change = null;
        if (testAddressChanges is not null && testAddressChanges.TryGetStorageChange(UInt256.Zero, out StorageChange? storageChange))
        {
            change = storageChange;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted, Is.True);
            Assert.That(change, Is.Not.Null,
                "EIP-7702 FastCall must succeed and propagate via SSTORE; missing slot 0 entry indicates the call failed.");
            Assert.That(change!.Value.Value, Is.EqualTo(UInt256.One.ToBigEndianWord()),
                "EIP-7702: delegation to a precompile must NOT execute the precompile - FastCall returns 1 regardless of forwarded gas.");
        }
    }

    /// <summary>
    /// EIP-7928: DELEGATECALL to a precompile records the precompile (codeSource) in BAL.
    /// </summary>
    /// <remarks>
    /// For DELEGATECALL/CALLCODE, target == ExecutingAccount, so the indirect <c>AccountExists(target)</c> records
    /// the caller, not the precompile. The decorator's <c>IsPrecompile</c> fast-path otherwise skips AddAccountRead.
    /// </remarks>
    [Test]
    public void DelegateCall_to_precompile_records_codeSource_in_BAL_under_PrecompileCachedCodeInfoRepository()
    {
        Address precompileAddress = Sha256Precompile.Address;
        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor, Block block) =
            SetupPrecompileBalScenario();

        byte[] code = Prepare.EvmCode.DelegateCall(precompileAddress, 50_000).Done;
        Transaction tx = BuildContractTx(code, _gasLimit, _testAccountBalance, block.Header);

        TransactionResult res = processor.Execute(tx, NullTxTracer.Instance);

        AccountChangesAtIndex? precompileChanges = tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(precompileAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted, Is.True);
            Assert.That(precompileChanges, Is.Not.Null,
                "DELEGATECALL codeSource (precompile) must be recorded in BAL even when the decorator's fast-path is active");
        }
    }

    /// <summary>
    /// EIP-7928: top-level transaction with <c>tx.to == precompile_address</c> records the recipient in BAL.
    /// </summary>
    /// <remarks>
    /// <see cref="TransactionProcessor"/>'s <c>BuildExecutionEnvironment</c> only calls <c>accessTracker.WarmUp</c> (EIP-2929)
    /// on the recipient. With <c>tx.value == 0</c> there is no incidental <c>AddBalanceChange</c> to create the entry.
    /// </remarks>
    [Test]
    public void Direct_transaction_to_precompile_records_recipient_in_BAL_under_PrecompileCachedCodeInfoRepository()
    {
        Address precompileAddress = Sha256Precompile.Address;
        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor, _) =
            SetupPrecompileBalScenario();

        Transaction tx = Build.A.Transaction
            .To(precompileAddress)
            .WithData([1, 2, 3])
            .WithGasLimit(50_000)
            .WithValue(UInt256.Zero)
            .SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;

        TransactionResult res = processor.Execute(tx, NullTxTracer.Instance);

        AccountChangesAtIndex? precompileChanges = tracedState.GetGeneratingBlockAccessList()!.GetAccountChanges(precompileAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted, Is.True);
            Assert.That(precompileChanges, Is.Not.Null,
                "Top-level transaction recipient that is a precompile must be recorded in BAL");
        }
    }

    private static IEnumerable<TestCaseData> PreValidationRejectionCases()
    {
        yield return new TestCaseData(
                0ul,
                (Func<ulong, Address, AuthorizationTuple>)((chainId, authority) =>
                    new(chainId, _delegationTargetAddress, ulong.MaxValue, 0, UInt256.One, UInt256.One, authority)))
            .SetName("EIP7702_authorization_max_nonce_keeps_authority_out_of_BAL_account_nonce_zero");
        yield return new TestCaseData(
                ulong.MaxValue,
                (Func<ulong, Address, AuthorizationTuple>)((chainId, authority) =>
                    new(chainId, _delegationTargetAddress, ulong.MaxValue, 0, UInt256.One, UInt256.One, authority)))
            .SetName("EIP7702_authorization_max_nonce_keeps_authority_out_of_BAL_account_nonce_max");
        yield return new TestCaseData(
                0ul,
                (Func<ulong, Address, AuthorizationTuple>)((chainId, authority) =>
                    new(chainId, _delegationTargetAddress, 0, 0, UInt256.One, SecP256k1Curve.HalfNPlusOne, authority)))
            .SetName("EIP7702_authorization_high_s_keeps_authority_out_of_BAL");
    }

    [TestCaseSource(nameof(PreValidationRejectionCases))]
    public void Eip7702_authorization_pre_validation_rejection_does_not_record_authority_or_target(
        ulong authorityNonce,
        Func<ulong, Address, AuthorizationTuple> buildAuthorization)
    {
        byte[] entryCode = BuildStorageWriteCode(UInt256.Zero, UInt256.One);
        Address authority = TestItem.AddressB;

        InitWorldState(TestState, entryCode);
        AddAccountToState(authority, authorityNonce);

        BlockAccessListAtIndex bal = ExecuteSetCodeCall(buildAuthorization(SpecProvider.ChainId, authority));

        using (Assert.EnterMultipleScope())
        {
            AssertNonceChange(bal, TestItem.AddressA, 1);
            AssertStorageChange(bal, _callTargetAddress, UInt256.Zero, UInt256.One);
            Assert.That(bal.GetAccountChanges(authority), Is.Null);
            Assert.That(bal.GetAccountChanges(_delegationTargetAddress), Is.Null);
        }
    }

    // Post-validation rejection: the cold authority lookup happens before the existing-code
    // check fails, so a pure account read is recorded for the authority.
    [Test]
    public void Eip7702_authorization_existing_code_rejection_records_authority_only()
    {
        byte[] entryCode = BuildStorageWriteCode(UInt256.Zero, UInt256.One);
        byte[] authorityCode = [(byte)Instruction.STOP];
        Address authority = TestItem.AddressB;

        InitWorldState(TestState, entryCode);
        AddAccountToState(authority, code: authorityCode);

        AuthorizationTuple authorization = SignAuthorization(TestItem.PrivateKeyB, _delegationTargetAddress);

        BlockAccessListAtIndex bal = ExecuteSetCodeCall(authorization);

        using (Assert.EnterMultipleScope())
        {
            AssertStorageChange(bal, _callTargetAddress, UInt256.Zero, UInt256.One);
            AssertPureAccountRead(bal.GetAccountChanges(authority));
            Assert.That(bal.GetAccountChanges(_delegationTargetAddress), Is.Null);
        }
    }

    [TestCase(true, TestName = "EIP7702_authorization_valid_nonce_records_signer_target_and_entry_change")]
    [TestCase(false, TestName = "EIP7702_authorization_invalid_nonce_records_signer_read_and_noop_sstore_read")]
    public void Eip7702_authorization_nonce_validity_records_bal(bool validAuthorizationNonce)
    {
        Address authority = TestItem.AddressB;
        byte[] entryCode = validAuthorizationNonce
            ? BuildCallResultStorageWriteCode(authority, UInt256.Zero)
            : BuildCallWithValueResultStorageWriteCode(authority, UInt256.Zero, UInt256.One);

        InitWorldState(TestState, entryCode, delegationTarget: _delegationTargetAddress);
        AddAccountToState(authority);
        if (validAuthorizationNonce)
        {
            AddAccountToState(_delegationTargetAddress, code: [(byte)Instruction.STOP]);
        }

        AuthorizationTuple authorization = SignAuthorization(
            TestItem.PrivateKeyB,
            _delegationTargetAddress,
            validAuthorizationNonce ? 0ul : 1ul);

        BlockAccessListAtIndex bal = ExecuteSetCodeCall(authorization);

        using (Assert.EnterMultipleScope())
        {
            if (validAuthorizationNonce)
            {
                AssertNonceChange(bal, authority, 1);
                AssertCodeChange(bal, authority, BuildDelegationCode(_delegationTargetAddress));
                AssertPureAccountRead(bal.GetAccountChanges(_delegationTargetAddress));
                AssertStorageChange(bal, _callTargetAddress, UInt256.Zero, UInt256.One);
            }
            else
            {
                AssertPureAccountRead(bal.GetAccountChanges(authority));
                Assert.That(bal.GetAccountChanges(_delegationTargetAddress), Is.Null);
                AssertStorageRead(bal, _callTargetAddress, UInt256.Zero);
            }
        }
    }

    [Test]
    public void Eip7702_null_address_delegation_to_empty_code_records_nonce_without_code_change()
    {
        InitWorldState(TestState, []);

        AuthorizationTuple authorization = SignAuthorization(TestItem.PrivateKeyA, Address.Zero, nonce: 1);

        BlockAccessListAtIndex bal = ExecuteSetCodeCall(authorization);
        AccountChangesAtIndex? senderChanges = bal.GetAccountChanges(TestItem.AddressA);

        using (Assert.EnterMultipleScope())
        {
            AssertNonceChange(bal, TestItem.AddressA, 2);
            Assert.That(senderChanges, Is.Not.Null);
            Assert.That(senderChanges!.CodeChange, Is.Null);
        }
    }

    [Test]
    public void Eip7702_multi_hop_delegation_resolves_one_hop_in_bal()
    {
        Address authority = TestItem.AddressB;
        Address intermediate = TestItem.AddressE;
        Address finalTarget = TestItem.AddressF;
        byte[] entryCode = BuildCallResultStorageWriteCode(authority, UInt256.Zero);

        InitWorldState(TestState, entryCode, delegationTarget: intermediate);
        AddAccountToState(authority);
        AddAccountToState(intermediate, code: BuildDelegationCode(finalTarget));
        AddAccountToState(finalTarget, code: [(byte)Instruction.STOP]);

        AuthorizationTuple authorization = SignAuthorization(TestItem.PrivateKeyB, intermediate);

        BlockAccessListAtIndex bal = ExecuteSetCodeCall(authorization);

        using (Assert.EnterMultipleScope())
        {
            AssertNonceChange(bal, authority, 1);
            AssertCodeChange(bal, authority, BuildDelegationCode(intermediate));
            AssertPureAccountRead(bal.GetAccountChanges(intermediate));
            Assert.That(bal.GetAccountChanges(finalTarget), Is.Null);
            AssertStorageRead(bal, _callTargetAddress, UInt256.Zero);
        }
    }

    [Test]
    public void Eip7702_self_delegation_resolves_one_hop_to_designator_code()
    {
        Address authority = TestItem.AddressB;
        byte[] entryCode = BuildCallResultStorageWriteCode(authority, UInt256.Zero);

        InitWorldState(TestState, entryCode, delegationTarget: authority);
        AddAccountToState(authority);

        AuthorizationTuple authorization = SignAuthorization(TestItem.PrivateKeyB, authority);

        BlockAccessListAtIndex bal = ExecuteSetCodeCall(authorization);

        using (Assert.EnterMultipleScope())
        {
            AssertNonceChange(bal, authority, 1);
            AssertCodeChange(bal, authority, BuildDelegationCode(authority));
            AssertStorageRead(bal, _callTargetAddress, UInt256.Zero);
        }
    }

    [Test]
    public void Eip7702_same_tx_delegation_chain_resolves_one_hop_in_bal()
    {
        Address authority = TestItem.AddressB;
        Address intermediate = TestItem.AddressE;
        Address finalTarget = TestItem.AddressF;
        byte[] entryCode = BuildCallResultStorageWriteCode(authority, UInt256.Zero);

        InitWorldState(TestState, entryCode, delegationTarget: intermediate);
        AddAccountToState(authority);
        AddAccountToState(intermediate);
        AddAccountToState(finalTarget, code: [(byte)Instruction.STOP]);

        AuthorizationTuple authorityAuthorization = SignAuthorization(TestItem.PrivateKeyB, intermediate);
        AuthorizationTuple intermediateAuthorization = SignAuthorization(TestItem.PrivateKeyE, finalTarget);

        BlockAccessListAtIndex bal = ExecuteSetCodeCall(authorityAuthorization, intermediateAuthorization);

        using (Assert.EnterMultipleScope())
        {
            AssertNonceChange(bal, authority, 1);
            AssertCodeChange(bal, authority, BuildDelegationCode(intermediate));
            AssertNonceChange(bal, intermediate, 1);
            AssertCodeChange(bal, intermediate, BuildDelegationCode(finalTarget));
            Assert.That(bal.GetAccountChanges(finalTarget), Is.Null);
            AssertStorageRead(bal, _callTargetAddress, UInt256.Zero);
        }
    }

    [TestCase(Instruction.CALL, TestName = "EIP7702_set_code_to_precompile_records_BAL_for_CALL")]
    [TestCase(Instruction.STATICCALL, TestName = "EIP7702_set_code_to_precompile_records_BAL_for_STATICCALL")]
    [TestCase(Instruction.DELEGATECALL, TestName = "EIP7702_set_code_to_precompile_records_BAL_for_DELEGATECALL")]
    [TestCase(Instruction.CALLCODE, TestName = "EIP7702_set_code_to_precompile_records_BAL_for_CALLCODE")]
    public void Eip7702_set_code_to_precompile_records_bal_for_call_opcode(Instruction callOpcode)
    {
        Address authority = TestItem.AddressB;
        Address precompile = Sha256Precompile.Address;
        byte[] entryCode = BuildCallOpcodeResultStorageWriteCode(callOpcode, authority, UInt256.Zero);

        InitWorldState(TestState, entryCode, delegationTarget: precompile);
        AddAccountToState(authority);

        AuthorizationTuple authorization = SignAuthorization(TestItem.PrivateKeyB, precompile);

        BlockAccessListAtIndex bal = ExecuteSetCodeCall(wrapPrecompileCache: true, authorization);

        using (Assert.EnterMultipleScope())
        {
            AssertNonceChange(bal, authority, 1);
            AssertCodeChange(bal, authority, BuildDelegationCode(precompile));
            AssertPureAccountRead(bal.GetAccountChanges(precompile));
            AssertStorageChange(bal, _callTargetAddress, UInt256.Zero, UInt256.One);
        }
    }

    [TestCase(Instruction.CREATE, Instruction.SLOAD, TestName = "EIP7928_create_sload_selfdestruct_records_storage_read")]
    [TestCase(Instruction.CREATE, Instruction.SSTORE, TestName = "EIP7928_create_sstore_selfdestruct_records_storage_read")]
    [TestCase(Instruction.CREATE2, Instruction.SLOAD, TestName = "EIP7928_create2_sload_selfdestruct_records_storage_read")]
    [TestCase(Instruction.CREATE2, Instruction.SSTORE, TestName = "EIP7928_create2_sstore_selfdestruct_records_storage_read")]
    public void Eip7928_same_tx_created_selfdestruct_records_storage_as_read(
        Instruction createOpcode,
        Instruction storageOpcode)
    {
        UInt256 slot = UInt256.One;
        Address beneficiary = TestItem.AddressB;
        byte[] childInitCode = storageOpcode == Instruction.SSTORE
            ? Prepare.EvmCode
                .PushData(1)
                .PushData(slot)
                .Op(Instruction.SSTORE)
                .SELFDESTRUCT(beneficiary)
                .Done
            : Prepare.EvmCode
                .PushData(slot)
                .Op(Instruction.SLOAD)
                .Op(Instruction.POP)
                .SELFDESTRUCT(beneficiary)
                .Done;
        byte[] salt = [0x01];
        UInt256 createdBalance = 100;
        Address createdAddress = createOpcode == Instruction.CREATE2
            ? ContractAddress.From(_callTargetAddress, salt.PadLeft(32), childInitCode)
            : ContractAddress.From(_callTargetAddress, 0);
        byte[] factoryCode = BuildCreateThenPopCode(createOpcode, childInitCode, salt, createdBalance);

        InitWorldState(TestState, factoryCode, callTargetBalance: createdBalance);

        BlockAccessListAtIndex bal = ExecuteCallTx(_callTargetAddress);
        AccountChangesAtIndex? createdChanges = bal.GetAccountChanges(createdAddress);
        AccountChangesAtIndex? beneficiaryChanges = bal.GetAccountChanges(beneficiary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(createdChanges, Is.Not.Null);
            Assert.That(createdChanges!.StorageReads, Does.Contain(slot));
            Assert.That(createdChanges.StorageChangeCount, Is.EqualTo(0));
            Assert.That(createdChanges.NonceChange, Is.Null);
            Assert.That(createdChanges.CodeChange, Is.Null);
            Assert.That(beneficiaryChanges, Is.Not.Null);
            Assert.That(beneficiaryChanges!.BalanceChange, Is.Not.Null);
            Assert.That(beneficiaryChanges.BalanceChange!.Value.Value, Is.EqualTo(createdBalance));
        }
    }

    [TestCase(0, TestName = "EIP7928_create2_selfdestruct_then_recreate_same_block_zero_balance")]
    [TestCase(100, TestName = "EIP7928_create2_selfdestruct_then_recreate_same_block_nonzero_balance")]
    public void Eip7928_create2_selfdestruct_then_recreate_same_block_records_fresh_changes(int firstCreateBalance)
    {
        Address beneficiary = TestItem.AddressB;
        byte[] runtimeCode = [(byte)Instruction.STOP];
        byte[] childInitCode = BuildFlagConditionalSelfdestructInitCode(beneficiary, runtimeCode);
        byte[] factoryCode = BuildCreate2ThenSetFlagCode(childInitCode);
        Address createdAddress = ContractAddress.From(_callTargetAddress, new byte[32], childInitCode);

        InitWorldState(TestState, factoryCode);

        Transaction firstTx = BuildCallTx(_callTargetAddress, value: (UInt256)firstCreateBalance);
        Transaction secondTx = BuildCallTx(_callTargetAddress, nonce: UInt256.One);

        // Two tx slices need to merge into a single BlockAccessListAtIndex view for the
        // per-tx assertion shape; ExecuteCallTxs materialises that combined slice.
        BlockAccessListAtIndex bal = ExecuteCallTxs(firstTx, secondTx);
        AccountChangesAtIndex? createdChanges = bal.GetAccountChanges(createdAddress);
        AccountChangesAtIndex? beneficiaryChanges = bal.GetAccountChanges(beneficiary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(createdChanges, Is.Not.Null);
            Assert.That(createdChanges!.NonceChange, Is.EqualTo((NonceChange?)new NonceChange(1, 1)));
            Assert.That(createdChanges.CodeChange, Is.Not.Null);
            Assert.That(createdChanges.CodeChange!.Value.Index, Is.EqualTo(1u));
            Assert.That(createdChanges.CodeChange.Value.Code, Is.EqualTo(runtimeCode));
            Assert.That(beneficiaryChanges, Is.Not.Null);
            if (firstCreateBalance == 0)
            {
                Assert.That(beneficiaryChanges!.BalanceChange, Is.Null);
            }
            else
            {
                Assert.That(beneficiaryChanges!.BalanceChange, Is.EqualTo((BalanceChange?)new BalanceChange(0, (UInt256)firstCreateBalance)));
            }
        }
    }

    [TestCaseSource(nameof(SelfdestructSendToSenderTestSource))]
    public void Eip7928_selfdestruct_to_sender_coalesces_sender_changes(IReleaseSpec spec, int victimBalance)
    {
        byte[] selfdestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.AddressA)
            .Done;

        InitWorldState(TestState, selfdestructCode, callTargetBalance: (UInt256)victimBalance);

        (TracedAccessWorldState tracedState, TransactionProcessor<EthereumGasPolicy> processor) = CreateTracedProcessor();
        BlockHeader header = Build.A.BlockHeader
            .WithGasLimit(120_000_000)
            .WithBaseFee(1)
            .TestObject;
        Transaction tx = BuildCallTx(_callTargetAddress);

        processor.SetBlockExecutionContext(new BlockExecutionContext(header, spec));
        CallOutputTracer tracer = new();
        TransactionResult res = processor.Execute(tx, tracer);
        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        UInt256 gasUsed = new((ulong)tracer.GasSpent);
        AccountChangesAtIndex? senderChanges = bal.GetAccountChanges(TestItem.AddressA);
        AccountChangesAtIndex? victimChanges = bal.GetAccountChanges(_callTargetAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(res.TransactionExecuted, Is.True, res.ToString());
            Assert.That(senderChanges, Is.Not.Null);
            Assert.That(senderChanges!.NonceChange, Is.EqualTo((NonceChange?)new NonceChange(0, 1)));
            Assert.That(senderChanges.BalanceChange,
                Is.EqualTo((BalanceChange?)new BalanceChange(0, _accountBalance - gasUsed + (UInt256)victimBalance)));
            Assert.That(victimChanges, Is.Not.Null);
            Assert.That(victimChanges!.NonceChange, Is.Null);
            Assert.That(victimChanges.CodeChange, Is.Null);
            if (victimBalance == 0)
            {
                Assert.That(victimChanges.BalanceChange, Is.Null);
            }
            else
            {
                Assert.That(victimChanges.BalanceChange, Is.EqualTo((BalanceChange?)new BalanceChange(0, UInt256.Zero)));
            }

            if (spec.SelfdestructOnlyOnSameTransaction)
            {
                Assert.That(TestState.AccountExists(_callTargetAddress), Is.True);
                Assert.That(TestState.GetBalance(_callTargetAddress), Is.EqualTo(UInt256.Zero));
                Assert.That(TestState.GetCode(_callTargetAddress), Is.EqualTo(selfdestructCode));
            }
            else
            {
                Assert.That(TestState.AccountExists(_callTargetAddress), Is.False);
            }
        }
    }

    [Test]
    public void Eip7928_extcodehash_records_boundary_account_reads()
    {
        Address emptyAccount = TestItem.AddressB;
        byte[] selfdestructInitCode = Prepare.EvmCode
            .SELFDESTRUCT(emptyAccount)
            .Done;
        byte[] salt = [0x01];
        Address createdEmptyAddress = ContractAddress.From(_callTargetAddress, 0);
        Address destroyedAddress = ContractAddress.From(_callTargetAddress, salt.PadLeft(32), selfdestructInitCode);
        byte[] factoryCode = Prepare.EvmCode
            .PushData(emptyAccount)
            .Op(Instruction.EXTCODEHASH)
            .Op(Instruction.POP)
            .Create([], 0)
            .Op(Instruction.POP)
            .PushData(createdEmptyAddress)
            .Op(Instruction.EXTCODEHASH)
            .Op(Instruction.POP)
            .Create2(selfdestructInitCode, salt, 0)
            .Op(Instruction.POP)
            .PushData(destroyedAddress)
            .Op(Instruction.EXTCODEHASH)
            .Op(Instruction.POP)
            .Done;

        InitWorldState(TestState, factoryCode);
        AddAccountToState(emptyAccount, balance: UInt256.One);

        BlockAccessListAtIndex bal = ExecuteCallTx(_callTargetAddress);
        AccountChangesAtIndex? emptyChanges = bal.GetAccountChanges(emptyAccount);
        AccountChangesAtIndex? createdEmptyChanges = bal.GetAccountChanges(createdEmptyAddress);
        AccountChangesAtIndex? destroyedChanges = bal.GetAccountChanges(destroyedAddress);

        using (Assert.EnterMultipleScope())
        {
            AssertPureAccountRead(emptyChanges);
            Assert.That(createdEmptyChanges, Is.Not.Null);
            Assert.That(createdEmptyChanges!.BalanceChange, Is.Null);
            Assert.That(createdEmptyChanges.NonceChange, Is.EqualTo((NonceChange?)new NonceChange(0, 1)));
            Assert.That(createdEmptyChanges.CodeChange, Is.Null);
            AssertPureAccountRead(destroyedChanges);
        }
    }

    private void InitWorldState(
        IWorldState worldState,
        byte[]? extraCode = null,
        Address? delegationTarget = null,
        UInt256 callTargetBalance = default)
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

        if (delegationTarget is null)
        {
            worldState.CreateAccount(_delegationTargetAddress, 0);
            worldState.InsertCode(_delegationTargetAddress, ValueKeccak.Compute(_delegatedCode), _delegatedCode, SpecProvider.GenesisSpec);
        }

        worldState.CreateAccount(_callTargetAddress, callTargetBalance);
        if (extraCode is not null)
        {
            ValueHash256 codeHash = ValueKeccak.Compute(extraCode);
            worldState.InsertCode(_callTargetAddress, codeHash, extraCode, SpecProvider.GenesisSpec);
        }
        else
        {
            Address target = delegationTarget ?? _delegationTargetAddress;
            byte[] delegationCode = [.. Eip7702Constants.DelegationHeader, .. target.Bytes];
            worldState.InsertCode(_callTargetAddress, ValueKeccak.Compute(delegationCode), delegationCode, SpecProvider.GenesisSpec);
        }

        worldState.Commit(SpecProvider.GenesisSpec);
        worldState.CommitTree(0);
        worldState.RecalculateStateRoot();
    }

    /// <summary>
    /// When an outer CALL into an EIP-7702-delegated EOA OOGs at the cold-access gas charge
    /// for the delegation target, the target's address must NOT appear in the BAL — only the
    /// call target (the EOA itself). Mirrors EELS's
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
        ulong intrinsicGas = IntrinsicGasCalculator.Calculate(templateTx, Amsterdam.Instance, block.Header.GasLimit).MinimalGas;
        // Enough gas to push CALL operands and reach the cold-access charge for the EOA, but
        // 1 gas short of the cold-access charge for its delegation target. CALL pushes 7 stack
        // operands (3 each of GasCostOf.VeryLow), pays GasCostOf.Call, then ConsumeAccountAccessGas
        // for codeSource (cold), then for delegated (cold) — we cap at codeSource cold + 1 short.
        ulong pushOperandsCost = 7 * GasCostOf.VeryLow;
        // EIP-8038 raised the cold account access charge to 3000 (ColdAccountAccess).
        ulong executionGas = pushOperandsCost + GasCostOf.Call + Eip8038Constants.ColdAccountAccess + GasCostOf.WarmStateRead - 1;

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
            // The CALL target (the delegated EOA itself) is loaded to resolve delegation, so it
            // IS in the BAL. The delegation target is gated behind the cold-access OOG, so it
            // MUST NOT appear when the CALL never reaches that point.
            Assert.That(bal.GetAccountChanges(_callTargetAddress), Is.Not.Null,
                "EIP-7702 delegated EOA must be recorded as the CALL target");
            Assert.That(bal.GetAccountChanges(_delegationTargetAddress), Is.Null,
                "EIP-7702 delegation target must not be recorded when CALL OOGs before its code is loaded");
        }
    }

    [TestCase(120_000_000UL, 30_000_000UL, true, TestName = "EIP2935_system_call_records_storage_change_when_state_gas_affordable")]
    [TestCase(120_000_000UL, 30_000UL, false, TestName = "EIP2935_system_call_records_no_storage_access_when_state_gas_not_affordable")]
    public void Eip2935_system_call_bal_respects_eip8037_state_gas(ulong blockGasLimit, ulong systemCallGasLimit, bool shouldStoreParentHash)
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
                Assert.That(storageEntry.Value.Value, Is.EqualTo(new StorageChange(0, new UInt256(parentHash.Bytes, isBigEndian: true)).Value));
                Assert.That(accountChanges.StorageReads, Is.Empty);
            }
        }
        else
        {
            // Under EIP-8037, an unaffordable state-gas attempt records neither a storage
            // change nor a read — see upstream's "Fix EIP-8037 reverted state gas accounting".
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accountChanges!.StorageChangeCount, Is.EqualTo(0));
                Assert.That(accountChanges.StorageReads, Is.Empty);
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
            // Under EIP-8037's higher state-gas cost, the contract-creation tx runs out of
            // state gas before SELFDESTRUCT commits — both contracts get touched but neither
            // balance change persists.
            changes = [new(_testAddress), new ReadOnlyAccountChanges(TestItem.AddressB)];
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
            code = Prepare.EvmCode
                .Create(createInitCode, 0)
                .Done;
            // Under EIP-8037's higher state-gas cost the nested CREATE runs out of state gas;
            // nothing beyond the outer contract being touched persists on the BAL.
            changes = [new ReadOnlyAccountChanges(_testAddress)];
            yield return new TestCaseData(changes, code, null, false) { TestName = "create" };

            byte[] create2Salt = new byte[32];
            create2Salt[^1] = 1;
            code = Prepare.EvmCode
                .Create2(createInitCode, create2Salt, 0)
                .Done;
            changes = [new ReadOnlyAccountChanges(_testAddress)];
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
                // EIP-8038 raised the cold storage access charge to 3000 (ColdStorageAccess); budget enough
                // to complete the SLOAD (recording the read) then OOG on the following PUSH.
                Eip8038Constants.ColdStorageAccess + GasCostOf.VeryLow + GasCostOf.Memory - 1,
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
                // EIP-8038 raised the cold account access charge to 3000 (ColdAccountAccess); budget enough
                // to complete the cold access to the beneficiary (recording it) then OOG on the send.
                GasCostOf.SelfDestructEip150 + Eip8038Constants.ColdAccountAccess + GasCostOf.VeryLow,
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

        ulong blockGasLimit = 100_000;
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
    public void CacheCodeInfoRepository_reads_prior_code_change_from_bal_world_state()
    {
        CacheCodeInfoRepository.Clear();

        byte[] priorCode = [(byte)Instruction.STOP];
        ReadOnlyAccountChanges priorChange = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithCodeChanges(new CodeChange(0, priorCode))
            .TestObject;
        ReadOnlyBlockAccessList suggestedBal = Build.A.BlockAccessList
            .WithAccountChanges(priorChange)
            .TestObject;

        BlockAccessListBasedWorldState balWorldState = new(TestState, LimboLogs.Instance);
        balWorldState.SetBlockAccessIndex(1);
        balWorldState.SetParentReader(TestState);
        balWorldState.Setup(Build.A.Block.WithBlockAccessList(suggestedBal).TestObject);

        CacheCodeInfoRepository repo = new(balWorldState, new EthereumPrecompileProvider());
        CodeInfo result = repo.GetCachedCodeInfo(TestItem.AddressA, false, Amsterdam.Instance, out Address? delegationAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delegationAddress, Is.Null);
            Assert.That(result.CodeSpan.ToArray(), Is.EqualTo(priorCode));
        }
    }

    [Test]
    public void CacheCodeInfoRepository_falls_back_to_parent_code_by_address_after_bal_hash_miss()
    {
        CacheCodeInfoRepository.Clear();

        byte[] parentCode = [(byte)Instruction.STOP];
        TestState.CreateAccount(TestItem.AddressA, 0);
        TestState.InsertCode(TestItem.AddressA, parentCode, SpecProvider.GenesisSpec);
        TestState.Commit(SpecProvider.GenesisSpec);

        ReadOnlyAccountChanges accountRead = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .TestObject;
        ReadOnlyBlockAccessList suggestedBal = Build.A.BlockAccessList
            .WithAccountChanges(accountRead)
            .TestObject;

        BlockAccessListBasedWorldState balWorldState = new(TestState, LimboLogs.Instance);
        balWorldState.SetBlockAccessIndex(0);
        balWorldState.SetParentReader(TestState);
        balWorldState.Setup(Build.A.Block.WithBlockAccessList(suggestedBal).TestObject);

        CacheCodeInfoRepository repo = new(balWorldState, new EthereumPrecompileProvider());
        CodeInfo result = repo.GetCachedCodeInfo(TestItem.AddressA, false, Amsterdam.Instance, out Address? delegationAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delegationAddress, Is.Null);
            Assert.That(result.CodeSpan.ToArray(), Is.EqualTo(parentCode));
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
