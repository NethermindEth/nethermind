// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-8360: TCREATE creates a contract whose code, nonce and storage exist only for the current transaction.
/// </summary>
public class Eip8360Tests : VirtualMachineTestsBase
{
    private static readonly IReleaseSpec EnabledSpec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8360Enabled = true };
    private static readonly ISpecProvider EnabledSpecProvider = new TestSpecProvider(EnabledSpec);

    private static readonly Address Factory = TestItem.AddressC;
    private static readonly Address Library = TestItem.AddressD;
    private static readonly Address Sink = TestItem.AddressE;
    private static readonly byte[] Salt = new UInt256(8360).ToBigEndian();
    private const ulong GasLimit = 5_000_000;
    private const long CallGas = 1_000_000;

    private static readonly byte[] EmptyInit = [(byte)Instruction.STOP];

    private EthereumEcdsa _ecdsa = null!;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
    protected override ISpecProvider SpecProvider => EnabledSpecProvider;

    public enum StorageWriter { Direct, DelegateCall, Call }

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        _ecdsa = new EthereumEcdsa(SpecProvider.ChainId);
        TestState.CreateAccount(Sender, 1000.Ether);
        // Pre-funded so value sent to it never pays EIP-8037 new-account state gas.
        TestState.CreateAccount(Sink, 1);
        TestState.Commit(Spec);
        TestState.CommitTree(0);
    }

    [TestCase("0x0000000000000000000000000000000000000000", 0, "00")]
    [TestCase("0xdeadbeef00000000000000000000000000000000", 8360, "6001600055")]
    public void Address_is_keccak_of_0xfe_prefixed_preimage(string deployerHex, int salt, string initCodeHex)
    {
        Address deployer = new(deployerHex);
        byte[] saltBytes = new UInt256((ulong)salt).ToBigEndian();
        byte[] initCode = Bytes.FromHexString(initCodeHex);
        Address expected = new(Keccak.Compute(Bytes.Concat([0xfe], deployer.Bytes, saltBytes, Keccak.Compute(initCode).Bytes)));

        Address actual = ContractAddress.FromTransientCreate(deployer, saltBytes, initCode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actual, Is.Not.EqualTo(ContractAddress.From(deployer, saltBytes, initCode)), "0xfe separates it from CREATE2");
        }
    }

    [Test]
    public void Tcreate_is_undefined_until_the_eip_is_enabled([Values] bool enabled)
    {
        InstallCode(Factory, TCreateAndStore(EmptyInit, 0));

        TestAllTracerWithOutput tracer = Run(spec: enabled ? EnabledSpec : Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(enabled ? StatusCode.Success : StatusCode.Failure));
            Assert.That(FactorySlot(0), Is.EqualTo(enabled ? ToWord(TCreateAddress(Factory, EmptyInit)) : UInt256.Zero));
        }
    }

    [Test]
    public void Deploys_and_runs_within_the_transaction_then_keeps_only_the_balance([Values(0, 5)] int endowment)
    {
        byte[] runtime = ReturnWord(Prepare.EvmCode.PushData(42));
        byte[] init = Prepare.EvmCode.ForInitOf(runtime).Done;
        Address tcreated = TCreateAddress(Factory, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(TCreateAndStore(init, (UInt256)endowment))
            .Data(CallAndStoreWord(tcreated, UInt256.Zero, slot: 1))
            .STOP()
            .Done, 100);

        Assert.That(Run().StatusCode, Is.EqualTo(StatusCode.Success));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FactorySlot(0), Is.EqualTo(ToWord(tcreated)), "TCREATE returned its address");
            Assert.That(FactorySlot(1), Is.EqualTo((UInt256)42), "code ran within the transaction");
            Assert.That(TestState.GetNonce(Factory), Is.EqualTo(1UL), "deployer nonce is not incremented");
            AssertFinalized(tcreated, (UInt256)endowment);
        }

        // The finalized account is a valid target again in the next transaction.
        ClearFactorySlot(0);
        Assert.That(Run().StatusCode, Is.EqualTo(StatusCode.Success));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FactorySlot(0), Is.EqualTo(ToWord(tcreated)));
            AssertFinalized(tcreated, (UInt256)(2 * endowment));
        }
    }

    [TestCase(StorageWriter.Direct, Instruction.SLOAD, 7, 0)]
    [TestCase(StorageWriter.Direct, Instruction.TLOAD, 7, 0)]
    [TestCase(StorageWriter.DelegateCall, Instruction.SLOAD, 7, 0)]
    [TestCase(StorageWriter.Call, Instruction.SLOAD, 0, 7)]
    public void Storage_in_a_tcreate_account_is_transient(StorageWriter writer, Instruction readBack, int expectedRead, int expectedLibraryStorage)
    {
        InstallCode(Library, Prepare.EvmCode.SSTORE(0, [7]).STOP().Done);
        byte[] write = writer switch
        {
            StorageWriter.Direct => Prepare.EvmCode.SSTORE(0, [7]).Done,
            StorageWriter.DelegateCall => Prepare.EvmCode.DelegateCall(Library, CallGas).Op(Instruction.POP).Done,
            _ => Prepare.EvmCode.Call(Library, CallGas).Op(Instruction.POP).Done,
        };
        byte[] runtime = ReturnWord(Prepare.EvmCode.PushData(0).Op(readBack));
        byte[] init = Prepare.EvmCode.Data(write).ForInitOf(runtime).Done;
        Address tcreated = TCreateAddress(Factory, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(TCreateAndStore(init, UInt256.Zero))
            .Data(CallAndStoreWord(tcreated, UInt256.Zero, slot: 1))
            .STOP()
            .Done);

        TestAllTracerWithOutput tracer = Run();

        // Two new persistent slots in every case: the factory's address and read-back slots, or, when the read-back
        // is zero, the factory's address slot and the called library's slot.
        const long expectedStateGas = 2 * GasCostOf.SSetState;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo((ulong)expectedStateGas), "no persistent slot is created");
            Assert.That(FactorySlot(1), Is.EqualTo((UInt256)expectedRead), "value read back within the transaction");
            Assert.That(Storage(tcreated, 0), Is.EqualTo(UInt256.Zero), "nothing reaches persistent storage");
            Assert.That(Storage(Library, 0), Is.EqualTo((UInt256)expectedLibraryStorage), "called accounts keep persistent storage");
        }
    }

    [Test]
    public void Sstore_and_sload_are_priced_as_tstore_and_tload()
    {
        InstallCode(Factory, TCreateStorageRoundTrip(Instruction.SSTORE, Instruction.SLOAD));
        TestAllTracerWithOutput persistentOpcodes = Run();
        InstallCode(Factory, TCreateStorageRoundTrip(Instruction.TSTORE, Instruction.TLOAD));
        TestAllTracerWithOutput transientOpcodes = Run();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistentOpcodes.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(persistentOpcodes.GasConsumedResult, Is.EqualTo(transientOpcodes.GasConsumedResult));
        }
    }

    [Test]
    public void Storage_and_code_of_a_tcreate_account_stay_out_of_the_block_access_list()
    {
        byte[] runtime = ReturnWord(Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD));
        byte[] init = Prepare.EvmCode.SSTORE(0, [7]).PushData(1).Op(Instruction.SLOAD).Op(Instruction.POP).ForInitOf(runtime).Done;
        Address tcreated = TCreateAddress(Factory, init);
        InstallCode(Factory, Prepare.EvmCode
            .TCreate(init, Salt, 3)
            .Op(Instruction.POP)
            .Call(tcreated, CallGas)
            .Op(Instruction.POP)
            .STOP()
            .Done, 10);

        TracedAccessWorldState tracedState = new(TestState, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, SpecProvider, tracedState,
            new EthereumVirtualMachine(new TestBlockhashProvider(SpecProvider), SpecProvider, LimboLogs.Instance),
            new EthereumCodeInfoRepository(tracedState), LimboLogs.Instance);

        Assert.That(Run(processor: processor).StatusCode, Is.EqualTo(StatusCode.Success));

        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        AccountChangesAtIndex? tcreatedChanges = bal.GetAccountChanges(tcreated);
        AccountChangesAtIndex? factoryChanges = bal.GetAccountChanges(Factory);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tcreatedChanges, Is.Not.Null, "the address is still accessed");
            Assert.That(tcreatedChanges!.StorageChanges, Is.Empty);
            Assert.That(tcreatedChanges.StorageReads, Is.Empty);
            Assert.That(tcreatedChanges.NonceChange, Is.Null, "finalization discards the nonce");
            Assert.That(tcreatedChanges.CodeChange, Is.Null, "finalization discards the code");
            Assert.That(tcreatedChanges.BalanceChange?.Value, Is.EqualTo((UInt256)3));
            Assert.That(factoryChanges!.NonceChange, Is.Null, "the deployer nonce is unchanged");
        }
    }

    [TestCase(null, 0, TestName = "CREATE2 in initcode halts")]
    [TestCase(Instruction.DELEGATECALL, 0, TestName = "CREATE2 reached through DELEGATECALL halts")]
    [TestCase(Instruction.CALLCODE, 0, TestName = "CREATE2 reached through CALLCODE halts")]
    [TestCase(Instruction.CALL, 1, TestName = "CREATE2 in a called account succeeds")]
    public void Create2_halts_in_a_tcreate_account_context(Instruction? via, int expectedCreate2Deployments)
    {
        byte[] create2 = Prepare.EvmCode.Create2(EmptyInit, Salt, 0).Op(Instruction.POP).STOP().Done;
        InstallCode(Library, create2);
        byte[] init = via switch
        {
            null => create2,
            Instruction.DELEGATECALL => HaltIfZero(Prepare.EvmCode.DelegateCall(Library, CallGas).Done),
            Instruction.CALLCODE => HaltIfZero(Prepare.EvmCode.CallCode(Library, CallGas).Done),
            _ => HaltIfZero(Prepare.EvmCode.Call(Library, CallGas).Done),
        };
        Address tcreated = TCreateAddress(Factory, init);
        Address create2Target = ContractAddress.From(via is null or Instruction.DELEGATECALL or Instruction.CALLCODE ? tcreated : Library, Salt, EmptyInit);
        InstallCode(Factory, TCreateAndStore(init, UInt256.Zero));

        Assert.That(Run().StatusCode, Is.EqualTo(StatusCode.Success));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FactorySlot(0), Is.EqualTo(expectedCreate2Deployments == 0 ? UInt256.Zero : ToWord(tcreated)));
            Assert.That(TestState.AccountExists(create2Target), Is.EqualTo(expectedCreate2Deployments == 1));
        }
    }

    [Test]
    public void Create_from_a_tcreate_account_deploys_a_persistent_contract_once()
    {
        byte[] childRuntime = [(byte)Instruction.PUSH0];
        byte[] init = HaltIfZero(Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf(childRuntime).Done, 0).Done);
        Address tcreated = TCreateAddress(Factory, init);
        // The TCREATE account's nonce is 1 when its initcode starts.
        Address child = ContractAddress.From(tcreated, 1);
        InstallCode(Factory, TCreateAndStore(init, UInt256.Zero));

        Assert.That(Run().StatusCode, Is.EqualTo(StatusCode.Success));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FactorySlot(0), Is.EqualTo(ToWord(tcreated)));
            Assert.That(TestState.GetCode(child), Is.EqualTo(childRuntime), "the CREATE child persists");
            Assert.That(TestState.GetNonce(child), Is.EqualTo(1UL));
            AssertFinalized(tcreated, UInt256.Zero);
        }

        // Recreated, the account derives the same CREATE address and collides (EIP-684).
        ClearFactorySlot(0);
        Assert.That(Run().StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(FactorySlot(0), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void Reverted_tcreate_is_no_longer_a_tcreate_account([Values] bool traced)
    {
        // The helper's TCREATE succeeds, then the helper reverts; a later value transfer to the address
        // must be priced as an ordinary new account, not through the TCREATE balance tables.
        byte[] helper = Prepare.EvmCode.TCreate(EmptyInit, Salt, 0).Op(Instruction.POP).Revert(0, 0).Done;
        InstallCode(Library, helper);
        Address reverted = TCreateAddress(Library, EmptyInit);
        InstallCode(Factory, Prepare.EvmCode
            .Call(Library, CallGas)
            .Op(Instruction.POP)
            .CallWithValue(reverted, CallGas, 1)
            .Op(Instruction.POP)
            .STOP()
            .Done, 10);

        GasConsumed gas = RunForGas(traced);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gas.BlockStateGas, Is.EqualTo((ulong)GasCostOf.NewAccountState));
            Assert.That(TestState.GetBalance(reverted), Is.EqualTo(UInt256.One));
            Assert.That(TestState.IsContract(reverted), Is.False);
        }
    }

    [Test]
    public void Tracking_is_journaled_with_the_frame()
    {
        using StackAccessTracker tracker = new();
        tracker.TakeSnapshot();
        tracker.WasTransientlyCreated(TestItem.AddressA, 5);
        Assert.That(tracker.IsTransientCreate(TestItem.AddressA), Is.True);

        tracker.Restore();

        Assert.That(tracker.IsTransientCreate(TestItem.AddressA), Is.False);
    }

    public static IEnumerable<TestCaseData> BalanceTableCases()
    {
        long newAccount = GasCostOf.NewAccountState;
        long accountWrite = (long)Eip8038Constants.AccountWrite;
        yield return new TestCaseData(0, 5, 0, 0, newAccount, 0L, 5).SetName("First balance from zero charges state gas");
        yield return new TestCaseData(0, 5, 5, 0, 0L, accountWrite, 0).SetName("Returning to a zero original refills state gas and refunds ACCOUNT_WRITE");
        yield return new TestCaseData(0, 5, 2, 0, newAccount, 0L, 3).SetName("Partial drain keeps the state charge");
        yield return new TestCaseData(3, 5, 0, 0, 0L, 0L, 8).SetName("Non-zero original balance pays no state gas");
        yield return new TestCaseData(3, 5, 5, 0, 0L, accountWrite, 3).SetName("Restoring a non-zero original balance refunds ACCOUNT_WRITE");
        yield return new TestCaseData(0, 0, 0, 4, newAccount, 0L, 4).SetName("Value CALLed into a TCREATE account charges state gas");
    }

    [TestCaseSource(nameof(BalanceTableCases))]
    public void Balance_changes_follow_the_eip_tables(int preFund, int endowment, int sentOut, int calledIn,
        long expectedStateGas, long expectedRefund, int expectedFinalBalance)
    {
        byte[] init = sentOut == 0
            ? EmptyInit
            : Prepare.EvmCode.CallWithValue(Sink, CallGas, (UInt256)sentOut).Op(Instruction.POP).STOP().Done;
        Address tcreated = TCreateAddress(Factory, init);
        if (preFund != 0)
        {
            TestState.CreateAccount(tcreated, (UInt256)preFund);
        }

        Prepare factory = Prepare.EvmCode.TCreate(init, Salt, (UInt256)endowment).Op(Instruction.POP);
        if (calledIn != 0)
        {
            factory.CallWithValue(tcreated, CallGas, (UInt256)calledIn).Op(Instruction.POP);
        }

        InstallCode(Factory, factory.STOP().Done, 100);

        TestAllTracerWithOutput tracer = Run();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo((ulong)expectedStateGas), "state gas");
            Assert.That(tracer.Refund, Is.EqualTo(expectedRefund), "ACCOUNT_WRITE refund");
            AssertFinalized(tcreated, (UInt256)expectedFinalBalance);
        }
    }

    [Test]
    public void Balance_tables_apply_without_instruction_tracing([Values] bool traced)
    {
        // Untraced value calls to code-less accounts take a frame-less fast path; draining through it must still refill.
        byte[] init = Prepare.EvmCode.CallWithValue(Sink, CallGas, 5).Op(Instruction.POP).STOP().Done;
        Address tcreated = TCreateAddress(Factory, init);
        InstallCode(Factory, Prepare.EvmCode.TCreate(init, Salt, 5).Op(Instruction.POP).STOP().Done, 100);

        GasConsumed gas = RunForGas(traced);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gas.BlockStateGas, Is.Zero);
            AssertFinalized(tcreated, UInt256.Zero);
        }
    }

    [Test]
    public void Endowment_pays_account_write_once()
    {
        InstallCode(Factory, Prepare.EvmCode.TCreate(EmptyInit, Salt, 0).Op(Instruction.POP).STOP().Done, 100);
        ulong withoutValue = Run().GasConsumedResult.BlockGas;
        InstallCode(Factory, Prepare.EvmCode.TCreate(EmptyInit, Salt, 5).Op(Instruction.POP).STOP().Done, 100);
        ulong withValue = Run().GasConsumedResult.BlockGas;

        Assert.That(withValue - withoutValue, Is.EqualTo(Eip8038Constants.AccountWrite));
    }

    [TestCase(0)]
    [TestCase(33)]
    public void Charges_base_cost_and_target_access_instead_of_create_costs(int deployedLength)
    {
        byte[] init = Prepare.EvmCode.Return(deployedLength, 0).Done;
        InstallCode(Factory, Prepare.EvmCode.Create2(init, Salt, 0).Op(Instruction.POP).STOP().Done);
        GasConsumed create2 = Run().GasConsumedResult;
        InstallCode(Factory, Prepare.EvmCode.TCreate(init, Salt, 0).Op(Instruction.POP).STOP().Done);
        GasConsumed tcreate = Run().GasConsumedResult;

        ulong codeDepositExecution = GasCostOf.CodeDepositExecutionPerWord * (ulong)((deployedLength + 31) / 32);
        long codeDepositState = GasCostOf.CodeDepositState * deployedLength;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(create2.BlockGas - tcreate.BlockGas,
                Is.EqualTo(Eip8038Constants.CreateAccess - (GasCostOf.TCreate + Eip8038Constants.ColdAccountAccess) + codeDepositExecution));
            Assert.That(tcreate.BlockStateGas, Is.Zero);
            Assert.That(create2.BlockStateGas, Is.EqualTo((ulong)(GasCostOf.CreateState + codeDepositState)));
        }
    }

    [Test]
    public void Parity_trace_reports_a_tcreate_action()
    {
        InstallCode(Factory, TCreateAndStore(EmptyInit, 0));
        (Block block, Transaction tx) = BuildFactoryCall();
        ParityLikeTxTracer tracer = new(block, tx, ParityTraceTypes.Trace);

        ExecuteFactoryCall(block, tx, tracer);

        ParityTraceAction create = tracer.BuildResult().Action!.Subtraces[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(create.CallType, Is.EqualTo("create"));
            Assert.That(create.CreationMethod, Is.EqualTo("tcreate"));
            Assert.That(create.To, Is.EqualTo(TCreateAddress(Factory, EmptyInit)));
        }
    }

    private static Address TCreateAddress(Address deployer, byte[] init) => ContractAddress.FromTransientCreate(deployer, Salt, init);

    private static UInt256 ToWord(Address address) => new(address.Bytes, isBigEndian: true);

    private static byte[] TCreateAndStore(byte[] init, in UInt256 value) =>
        Prepare.EvmCode.TCreate(init, Salt, value).PushData(0).Op(Instruction.SSTORE).Done;

    private static byte[] TCreateStorageRoundTrip(Instruction store, Instruction load) =>
        Prepare.EvmCode
            .TCreate(Prepare.EvmCode.PushData(7).PushData(0).Op(store).PushData(0).Op(load).Op(Instruction.POP).STOP().Done, Salt, 0)
            .Op(Instruction.POP)
            .STOP()
            .Done;

    private static byte[] ReturnWord(Prepare pushWord) =>
        pushWord.PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;

    private static byte[] CallAndStoreWord(Address target, in UInt256 value, int slot) =>
        Prepare.EvmCode
            .PushData(32).PushData(0).PushData(0).PushData(0).PushData(value).PushData(target).PushData(CallGas)
            .Op(Instruction.CALL)
            .Op(Instruction.POP)
            .PushData(0).Op(Instruction.MLOAD)
            .PushData(slot).Op(Instruction.SSTORE)
            .Done;

    /// <summary>Runs <paramref name="prefix"/>, which leaves a success flag, and halts when the flag is zero.</summary>
    private static byte[] HaltIfZero(byte[] prefix)
    {
        byte[] head = Prepare.EvmCode.Data(prefix).Op(Instruction.ISZERO).Done;
        byte jumpDest = (byte)(head.Length + 4);
        return Prepare.EvmCode
            .Data(head)
            .Op(Instruction.PUSH1).Data(jumpDest)
            .Op(Instruction.JUMPI)
            .STOP()
            .JUMPDEST()
            .Op(Instruction.INVALID)
            .Done;
    }

    private void InstallCode(Address address, byte[] code, int balance = 0)
    {
        if (TestState.AccountExists(address))
        {
            TestState.DeleteAccount(address);
        }

        TestState.CreateAccount(address, (UInt256)balance, 1);
        TestState.InsertCode(address, code, Spec);
        TestState.Commit(Spec);
    }

    private TestAllTracerWithOutput Run(IReleaseSpec? spec = null, ITransactionProcessor? processor = null)
    {
        // Access tracing warms every touched account, which would hide the cold TCREATE target access.
        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = false;
        ExecuteFactoryCall(tracer, spec, processor);
        return tracer;
    }

    private GasConsumed RunForGas(bool traced)
    {
        if (traced)
        {
            TestAllTracerWithOutput tracer = Run();
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            return tracer.GasConsumedResult;
        }

        ReceiptTracer receipt = new();
        ExecuteFactoryCall(receipt, spec: null, processor: null);
        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        return receipt.GasConsumed;
    }

    private void ExecuteFactoryCall(ITxTracer tracer, IReleaseSpec? spec, ITransactionProcessor? processor)
    {
        (Block block, Transaction tx) = BuildFactoryCall();
        ExecuteFactoryCall(block, tx, tracer, spec, processor);
    }

    private void ExecuteFactoryCall(Block block, Transaction tx, ITxTracer tracer, IReleaseSpec? spec = null, ITransactionProcessor? processor = null)
    {
        spec ??= Spec;
        (processor ?? _processor).Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);
        TestState.Commit(spec);
    }

    private (Block Block, Transaction Tx) BuildFactoryCall()
    {
        Transaction tx = Build.A.Transaction.WithTo(Factory).WithGasLimit(GasLimit).WithNonce(TestState.GetNonce(Sender))
            .SignedAndResolved(_ecdsa, SenderKey).TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber).WithTimestamp(Timestamp).WithGasLimit(2 * GasLimit)
            .WithTransactions(tx).TestObject;
        return (block, tx);
    }

    private UInt256 FactorySlot(int slot) => Storage(Factory, slot);

    private UInt256 Storage(Address address, int slot)
    {
        TestState.Get(new StorageCell(address, (UInt256)slot), out UInt256 value);
        return value;
    }

    private void ClearFactorySlot(int slot)
    {
        TestState.Set(new StorageCell(Factory, (UInt256)slot), UInt256.Zero);
        TestState.Commit(Spec);
    }

    private void AssertFinalized(Address address, in UInt256 expectedBalance)
    {
        Assert.That(TestState.AccountExists(address), Is.EqualTo(!expectedBalance.IsZero), "empty accounts are removed (EIP-161)");
        Assert.That(TestState.GetBalance(address), Is.EqualTo(expectedBalance), "balance is preserved");
        Assert.That(TestState.GetNonce(address), Is.EqualTo(0UL), "nonce is reset");
        Assert.That(TestState.IsContract(address), Is.False, "code is removed");
    }

    private sealed class ReceiptTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public byte StatusCode { get; private set; }
        public GasConsumed GasConsumed { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            GasConsumed = gasSpent;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            GasConsumed = gasSpent;
            StatusCode = Evm.StatusCode.Failure;
        }
    }
}
