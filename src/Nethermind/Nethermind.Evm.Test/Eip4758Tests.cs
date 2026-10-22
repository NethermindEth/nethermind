// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Autofac;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-4758: SELFDESTRUCT acts as SENDALL. It sweeps the executing account's balance to the beneficiary and halts,
/// and never destroys the account, not even one created in the same transaction.
/// </summary>
/// <remarks>
/// Scenarios follow the execution-specs EIP-4758 suite (tests/amsterdam/eip4758_sendall). Each runs on Bogota with
/// the EIP enabled by override; <see cref="DisabledSpec"/> is Bogota itself, where EIP-6780 and EIP-8246 apply.
/// </remarks>
public class Eip4758Tests : VirtualMachineTestsBase
{
    private static readonly IReleaseSpec EnabledSpec = new OverridableReleaseSpec(Bogota.Instance) { IsEip4758Enabled = true };
    private static readonly IReleaseSpec DisabledSpec = Bogota.Instance;
    private static readonly ISpecProvider EnabledSpecProvider = new TestSpecProvider(EnabledSpec);

    private static readonly Address Factory = TestItem.AddressC;
    private static readonly Address OtherFactory = new("0x4758000000000000000000000000000000000001");
    private static readonly Address Entry = new("0x4758000000000000000000000000000000000002");
    private static readonly Address OtherEntry = new("0x4758000000000000000000000000000000000003");
    private static readonly Address Existing = TestItem.AddressE;
    private static readonly Address Fresh = TestItem.AddressF;
    private static readonly Address OtherFresh = new("0x4758000000000000000000000000000000000004");
    private static readonly byte[] Salt = new byte[32];
    private static readonly UInt256 VictimBalance = 0x1234;
    private static readonly UInt256 Canary = 0xC0DE;
    private static readonly UInt256 SlotCanary = 0xDEAD;
    private const ulong GasLimit = 10_000_000;
    private const long CallGas = 1_000_000;
    private const long FactoryCallGas = 3_000_000;

    private EthereumEcdsa _ecdsa = null!;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => 1;
    protected override ISpecProvider SpecProvider => EnabledSpecProvider;

    public enum BeneficiaryKind { Fresh, Existing, Self }

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        _ecdsa = new EthereumEcdsa(SpecProvider.ChainId);
        TestState.CreateAccount(Sender, 1000.Ether);
        TestState.CreateAccount(Existing, 1);
        TestState.Commit(Spec);
        TestState.CommitTree(0);
    }

    [Test]
    public void Bogota_does_not_bundle_it() => Assert.That(Bogota.Instance.IsEip4758Enabled, Is.False);

    [Test]
    public void Created_account_persists(
        [Values(Instruction.CREATE, Instruction.CREATE2)] Instruction createOpcode,
        [Values] BeneficiaryKind kind,
        [Values] bool enabled)
    {
        Address? target = kind switch { BeneficiaryKind.Fresh => Fresh, BeneficiaryKind.Existing => Existing, _ => null };
        byte[] victimCode = SstoreCanaryThenSendAll(target);
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, createOpcode, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(createOpcode, init, VictimBalance, slot: 0))
            .Data(CallAndStore(victim, slot: 1))
            .STOP()
            .Done, VictimBalance);

        Assert.That(Run(Factory, enabled ? EnabledSpec : DisabledSpec).StatusCode, Is.EqualTo(StatusCode.Success));

        UInt256 victimBalance = kind == BeneficiaryKind.Self ? VictimBalance : UInt256.Zero;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Storage(Factory, 0), Is.EqualTo(ToWord(victim)));
            Assert.That(Storage(Factory, 1), Is.EqualTo(UInt256.One));
            if (enabled)
            {
                AssertPersisted(victim, victimCode, victimBalance, Canary);
            }
            else if (kind == BeneficiaryKind.Self)
            {
                // EIP-8246: the destroyed account keeps only its balance.
                AssertBalanceOnly(victim, VictimBalance);
            }
            else
            {
                Assert.That(TestState.AccountExists(victim), Is.False);
            }

            if (target is not null)
            {
                UInt256 held = kind == BeneficiaryKind.Existing ? UInt256.One : UInt256.Zero;
                Assert.That(TestState.GetBalance(target), Is.EqualTo(held + VictimBalance));
            }
        }
    }

    [Test]
    public void Gas_matches_the_destroying_selfdestruct([Values] BeneficiaryKind kind)
    {
        // Same code at two factories so both runs see identical cold accesses; only the spec differs.
        GasConsumed disabled = RunCreateAndSendAll(Factory, kind == BeneficiaryKind.Fresh ? Fresh : null, kind, DisabledSpec);
        GasConsumed enabled = RunCreateAndSendAll(OtherFactory, kind == BeneficiaryKind.Fresh ? OtherFresh : null, kind, EnabledSpec);

        Assert.That(enabled, Is.EqualTo(disabled));
    }

    [Test]
    public void Sendall_in_initcode_completes_the_deployment([Values(Instruction.CREATE, Instruction.CREATE2)] Instruction createOpcode, [Values] bool enabled)
    {
        byte[] init = SstoreCanaryThenSendAll(Existing);
        Address victim = CreatedAddress(Factory, createOpcode, init);
        InstallCode(Factory, Prepare.EvmCode.Data(CreateAndStore(createOpcode, init, VictimBalance, slot: 0)).STOP().Done, VictimBalance);

        Assert.That(Run(Factory, enabled ? EnabledSpec : DisabledSpec).StatusCode, Is.EqualTo(StatusCode.Success));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Storage(Factory, 0), Is.EqualTo(ToWord(victim)));
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + VictimBalance));
            if (enabled)
            {
                AssertPersisted(victim, [], UInt256.Zero, Canary);
            }
            else
            {
                Assert.That(TestState.AccountExists(victim), Is.False);
            }
        }
    }

    [Test]
    public void Sendall_in_create_transaction_initcode_completes_the_deployment([Values] bool enabled)
    {
        byte[] init = SstoreCanaryThenSendAll(Existing);
        Address created = ContractAddress.From(Sender, TestState.GetNonce(Sender));
        Transaction tx = Build.A.Transaction.WithCode(init).WithValue(VictimBalance).WithGasLimit(GasLimit)
            .WithNonce(TestState.GetNonce(Sender)).SignedAndResolved(_ecdsa, SenderKey).TestObject;

        Assert.That(Execute(tx, enabled ? EnabledSpec : DisabledSpec).StatusCode, Is.EqualTo(StatusCode.Success));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + VictimBalance));
            if (enabled)
            {
                AssertPersisted(created, [], UInt256.Zero, Canary);
            }
            else
            {
                Assert.That(TestState.AccountExists(created), Is.False);
            }
        }
    }

    [Test]
    public void Create2_onto_a_swept_account_collides()
    {
        byte[] victimCode = Prepare.EvmCode.SELFDESTRUCT(Existing).Done;
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, Instruction.CREATE2, init);
        InstallCode(Factory, Prepare.EvmCode.Data(Create2AndStoreAtCalldataSlot(init)).STOP().Done, 2 * VictimBalance);
        SetCanarySlots(Factory, 4);
        InstallCode(Entry, CallFactoryForSlots(0, 1));
        InstallCode(OtherEntry, CallFactoryForSlots(2, 3));

        // The retry in the creating transaction collides, and so do both attempts after the sweep.
        Assert.That(Run(Entry, EnabledSpec).StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(Run(victim, EnabledSpec).StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(Run(OtherEntry, EnabledSpec).StatusCode, Is.EqualTo(StatusCode.Success));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Storage(Factory, 0), Is.EqualTo(ToWord(victim)));
            Assert.That(Storage(Factory, 1), Is.EqualTo(UInt256.Zero));
            Assert.That(Storage(Factory, 2), Is.EqualTo(UInt256.Zero));
            Assert.That(Storage(Factory, 3), Is.EqualTo(UInt256.Zero));
            AssertPersisted(victim, victimCode, UInt256.Zero);
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + VictimBalance));
        }
    }

    [Test]
    public void Create2_after_a_same_transaction_sweep_collides_in_later_transactions([Values] bool enabled)
    {
        IReleaseSpec spec = enabled ? EnabledSpec : DisabledSpec;
        byte[] victimCode = SstoreCanaryThenSendAll(Existing);
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, Instruction.CREATE2, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(Create2AndStoreAtCalldataSlot(init))
            .Call(victim, CallGas)
            .Op(Instruction.POP)
            .STOP()
            .Done, 2 * VictimBalance);
        SetCanarySlots(Factory, 2);
        InstallCode(Entry, CallFactoryForSlots(0));
        InstallCode(OtherEntry, CallFactoryForSlots(1));

        Assert.That(Run(Entry, spec).StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(Run(OtherEntry, spec).StatusCode, Is.EqualTo(StatusCode.Success));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Storage(Factory, 0), Is.EqualTo(ToWord(victim)));
            if (enabled)
            {
                Assert.That(Storage(Factory, 1), Is.EqualTo(UInt256.Zero), "the persisted account blocks the address");
                AssertPersisted(victim, victimCode, UInt256.Zero, Canary);
                Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + VictimBalance));
            }
            else
            {
                Assert.That(Storage(Factory, 1), Is.EqualTo(ToWord(victim)), "EIP-6780 freed the address");
                Assert.That(TestState.AccountExists(victim), Is.False);
                Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + 2 * VictimBalance));
            }
        }
    }

    [Test]
    public void Sendall_fails_in_a_static_context()
    {
        byte[] victimCode = Prepare.EvmCode.SELFDESTRUCT(Fresh).Done;
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, Instruction.CREATE, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(Instruction.CREATE, init, VictimBalance, slot: 0))
            .StaticCall(victim, CallGas)
            .Op(Instruction.ISZERO)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done, VictimBalance);

        Assert.That(Run(Factory, EnabledSpec).StatusCode, Is.EqualTo(StatusCode.Success));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Storage(Factory, 1), Is.EqualTo(UInt256.One), "the STATICCALL fails");
            AssertPersisted(victim, victimCode, VictimBalance);
            Assert.That(TestState.AccountExists(Fresh), Is.False);
        }
    }

    [Test]
    public void Repeated_sendall_sweeps_once_and_both_succeed()
    {
        byte[] victimCode = Prepare.EvmCode.SELFDESTRUCT(Existing).Done;
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, Instruction.CREATE, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(Instruction.CREATE, init, VictimBalance, slot: 0))
            .Data(CallAndStore(victim, slot: 1))
            .Data(CallAndStore(victim, slot: 2))
            .STOP()
            .Done, VictimBalance);

        TestAllTracerWithOutput tracer = Run(Factory, EnabledSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(Storage(Factory, 1), Is.EqualTo(UInt256.One));
            Assert.That(Storage(Factory, 2), Is.EqualTo(UInt256.One));
            AssertPersisted(victim, victimCode, UInt256.Zero);
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + VictimBalance));
        }
    }

    [Test]
    public void State_gas_for_a_fresh_beneficiary_is_charged_only_for_a_nonzero_sweep([Values(0, 0x1234)] int balance)
    {
        // Two runs differing only in whether the beneficiary exists.
        GasConsumed toFresh = RunCreateAndSendAll(Factory, Fresh, BeneficiaryKind.Fresh, EnabledSpec, (UInt256)balance);
        GasConsumed toExisting = RunCreateAndSendAll(OtherFactory, Existing, BeneficiaryKind.Existing, EnabledSpec, (UInt256)balance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TestState.AccountExists(Fresh), Is.EqualTo(balance != 0), "a zero sweep creates no account");
            Assert.That(toFresh.BlockStateGas - toExisting.BlockStateGas, Is.EqualTo(balance == 0 ? 0UL : (ulong)GasCostOf.NewAccountState));
            Assert.That(toFresh.BlockGas - toExisting.BlockGas, Is.EqualTo(balance == 0 ? 0UL : Eip8038Constants.AccountWrite));
        }
    }

    [Test]
    public void Emits_a_transfer_log_only_for_value_moved_to_another_account([Values] BeneficiaryKind kind, [Values(0, 0x1234)] int balance)
    {
        Address? target = kind switch { BeneficiaryKind.Fresh => Fresh, BeneficiaryKind.Existing => Existing, _ => null };
        byte[] init = InitOf(SstoreCanaryThenSendAll(target));
        Address victim = CreatedAddress(Factory, Instruction.CREATE, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(Instruction.CREATE, init, (UInt256)balance, slot: 0))
            .Data(CallAndStore(victim, slot: 1))
            .STOP()
            .Done, (UInt256)balance);

        LogTracer tracer = new();
        Execute(BuildTx(Factory), EnabledSpec, tracer);

        List<LogEntry> expected = [];
        if (balance != 0)
        {
            expected.Add(TransferLog.CreateTransfer(Factory, victim, (UInt256)balance));
            if (target is not null) expected.Add(TransferLog.CreateTransfer(victim, target, (UInt256)balance));
        }

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        tracer.Logs.AssertEquivalentTo([.. expected]);
    }

    [Test]
    public void Value_received_after_the_sweep_stays_with_the_live_account()
    {
        // Sweeps when called without value, otherwise accepts it.
        byte[] victimCode = Prepare.EvmCode
            .CALLVALUE()
            .Op(Instruction.ISZERO)
            .PushData(6)
            .JUMPI()
            .STOP()
            .JUMPDEST()
            .SELFDESTRUCT(Existing)
            .Done;
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, Instruction.CREATE, init);
        UInt256 later = 7;
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(Instruction.CREATE, init, VictimBalance, slot: 0))
            .Data(CallAndStore(victim, slot: 1))
            .CallWithValue(victim, CallGas, later)
            .PushData(2)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done, VictimBalance + later);

        LogTracer tracer = new();
        Execute(BuildTx(Factory), EnabledSpec, tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(Storage(Factory, 2), Is.EqualTo(UInt256.One));
            AssertPersisted(victim, victimCode, later);
            // No burn or self-destruct log: nothing is finalized at the end of the transaction.
            tracer.Logs.AssertEquivalentTo(
            [
                TransferLog.CreateTransfer(Factory, victim, VictimBalance),
                TransferLog.CreateTransfer(victim, Existing, VictimBalance),
                TransferLog.CreateTransfer(Factory, victim, later),
            ]);
        }
    }

    [Test]
    public void Pre_existing_account_behaves_as_under_eip_6780([Values] bool enabled)
    {
        byte[] victimCode = SstoreCanaryThenSendAll(Existing);
        InstallCode(Entry, victimCode, VictimBalance);

        Assert.That(Run(Entry, enabled ? EnabledSpec : DisabledSpec).StatusCode, Is.EqualTo(StatusCode.Success));

        using (Assert.EnterMultipleScope())
        {
            AssertPersisted(Entry, victimCode, UInt256.Zero, Canary);
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + VictimBalance));
        }
    }

    [Test]
    public void Removes_the_selfdestruct_refund([Values] bool enabled)
    {
        // Berlin still refunds a destroyed account; EIP-4758 destroys none, so it refunds none.
        IReleaseSpec spec = new OverridableReleaseSpec(Berlin.Instance) { IsEip4758Enabled = enabled };
        byte[] victimCode = Prepare.EvmCode.SELFDESTRUCT(Existing).Done;
        InstallCode(Entry, victimCode, VictimBalance);

        TestAllTracerWithOutput tracer = Run(Entry, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(spec.GasCosts.DestroyRefund, Is.EqualTo(enabled ? 0UL : RefundOf.DestroyBeforeEip3529));
            Assert.That(tracer.Refund, Is.EqualTo(enabled ? 0L : (long)RefundOf.DestroyBeforeEip3529));
            Assert.That(TestState.IsContract(Entry), Is.EqualTo(enabled));
        }
    }

    [Test]
    public void Same_transaction_selfdestruct_is_deleted_only_before_the_transition()
    {
        const ulong transition = 15_000;
        CustomSpecProvider provider = new((new ForkActivation(0, 0), DisabledSpec), (new ForkActivation(BlockNumber, transition), EnabledSpec));
        byte[] victimCode = SstoreCanaryThenSendAll(Existing);
        byte[] init = InitOf(victimCode);
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(Instruction.CREATE, init, VictimBalance, slot: 0))
            .PushData(0).Op(Instruction.SLOAD)
            .Data(CallStackTarget())
            .STOP()
            .Done, 3 * VictimBalance);

        Address[] victims = new Address[3];
        ulong[] timestamps = [transition - 1, transition, transition + 1];
        for (int i = 0; i < timestamps.Length; i++)
        {
            victims[i] = ContractAddress.From(Factory, TestState.GetNonce(Factory));
            Assert.That(Run(Factory, provider.GetSpec(new ForkActivation(BlockNumber, timestamps[i])), timestamps[i]).StatusCode, Is.EqualTo(StatusCode.Success));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TestState.AccountExists(victims[0]), Is.False);
            AssertPersisted(victims[1], victimCode, UInt256.Zero, Canary);
            AssertPersisted(victims[2], victimCode, UInt256.Zero, Canary);
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + 3 * VictimBalance));
        }
    }

    [Test]
    public void Create2_address_freed_before_the_transition_can_be_reused_once()
    {
        const ulong transition = 15_000;
        CustomSpecProvider provider = new((new ForkActivation(0, 0), DisabledSpec), (new ForkActivation(BlockNumber, transition), EnabledSpec));
        byte[] victimCode = SstoreCanaryThenSendAll(Existing);
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, Instruction.CREATE2, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(Create2AndStoreAtCalldataSlot(init))
            .Call(victim, CallGas)
            .Op(Instruction.POP)
            .STOP()
            .Done, 3 * VictimBalance);
        SetCanarySlots(Factory, 3);
        Address[] entries = [Entry, OtherEntry, new("0x4758000000000000000000000000000000000005")];
        ulong[] timestamps = [transition - 1, transition, transition + 1];
        for (int i = 0; i < entries.Length; i++)
        {
            InstallCode(entries[i], CallFactoryForSlots(i));
            Assert.That(Run(entries[i], provider.GetSpec(new ForkActivation(BlockNumber, timestamps[i])), timestamps[i]).StatusCode, Is.EqualTo(StatusCode.Success));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Storage(Factory, 0), Is.EqualTo(ToWord(victim)));
            Assert.That(Storage(Factory, 1), Is.EqualTo(ToWord(victim)), "the pre-transition deletion freed the address");
            Assert.That(Storage(Factory, 2), Is.EqualTo(UInt256.Zero), "the post-transition account persists");
            AssertPersisted(victim, victimCode, UInt256.Zero, Canary);
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo(1 + 2 * VictimBalance));
        }
    }

    [Test]
    public void Tcreate_account_that_sends_all_is_finalized_by_eip_8360([Values] bool enabled)
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip8360Enabled = true, IsEip4758Enabled = enabled };
        byte[] init = Prepare.EvmCode.SELFDESTRUCT(Existing).Done;
        Address tcreated = ContractAddress.FromTransientCreate(Factory, Salt, init);
        UInt256 later = 3;
        InstallCode(Factory, Prepare.EvmCode
            .TCreate(init, Salt, 5)
            .Op(Instruction.POP)
            .CallWithValue(tcreated, CallGas, later)
            .Op(Instruction.POP)
            .STOP()
            .Done, 100);

        Assert.That(Run(Factory, spec).StatusCode, Is.EqualTo(StatusCode.Success));

        // Either way the account ends the transaction holding only what it received after the sweep.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo((UInt256)6));
            AssertBalanceOnly(tcreated, later);
        }
    }

    [Test]
    public void Block_access_list_keeps_the_writes_of_a_swept_account([Values] bool enabled)
    {
        byte[] victimCode = SstoreCanaryThenSendAll(Existing);
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, Instruction.CREATE, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(Instruction.CREATE, init, VictimBalance, slot: 0))
            .Data(CallAndStore(victim, slot: 1))
            .STOP()
            .Done, VictimBalance);
        IReleaseSpec spec = enabled ? EnabledSpec : DisabledSpec;

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(new TestSpecProvider(spec))
            .Build();
        TracedAccessWorldState tracedState = new(TestState, parallel: false);
        using ILifetimeScope scope = container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldState>(tracedState));
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        ITransactionProcessor processor = scope.Resolve<ITransactionProcessor>();
        TestAllTracerWithOutput tracer = CreateTracer();
        Execute(BuildTx(Factory), spec, tracer, processor);

        BlockAccessListAtIndex bal = tracedState.GetGeneratingBlockAccessList()!;
        AccountChangesAtIndex? victimChanges = bal.GetAccountChanges(victim);
        AccountChangesAtIndex? beneficiaryChanges = bal.GetAccountChanges(Existing);
        Assert.That(victimChanges, Is.Not.Null);
        Assert.That(beneficiaryChanges, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(victimChanges!.BalanceChange, Is.Null, "funded and swept within the transaction");
            Assert.That(beneficiaryChanges!.BalanceChange?.Value, Is.EqualTo(1 + VictimBalance));
            if (enabled)
            {
                Assert.That(victimChanges.StorageChangeCount, Is.EqualTo(1), "the write survives as a write");
                Assert.That(victimChanges.StorageReads, Is.Empty);
                Assert.That(victimChanges.NonceChange?.Value, Is.EqualTo(1UL));
                Assert.That(victimChanges.CodeChange?.Code, Is.EqualTo(victimCode));
            }
            else
            {
                Assert.That(victimChanges.StorageChangeCount, Is.Zero, "a destroyed account's write becomes a read");
                Assert.That(victimChanges.StorageReads, Does.Contain(UInt256.Zero));
                Assert.That(victimChanges.NonceChange, Is.Null);
                Assert.That(victimChanges.CodeChange, Is.Null);
            }
        }
    }

    [Test]
    public void Call_and_parity_tracers_report_the_sweep()
    {
        byte[] victimCode = SstoreCanaryThenSendAll(Existing);
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, Instruction.CREATE, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(Instruction.CREATE, init, VictimBalance, slot: 0))
            .Data(CallAndStore(victim, slot: 1))
            .STOP()
            .Done, VictimBalance);
        Transaction tx = BuildTx(Factory);
        Block block = BuildBlock(tx);

        ParityLikeTxTracer parity = new(block, tx, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
        using NativeCallTracer call = new(tx, EnabledSpec, GethTraceOptions.Default with { Tracer = NativeCallTracer.CallTracer });
        Execute(tx, EnabledSpec, new CompositeTxTracer(parity, call));

        ParityLikeTxTrace parityTrace = parity.BuildResult();
        ParityTraceAction suicide = parityTrace.Action!.Subtraces[1].Subtraces[0];
        using GethLikeTxTrace callTrace = call.BuildResult();
        string json = JsonSerializer.Serialize(callTrace.CustomTracerResult!.Value, EthereumJsonSerializer.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement sweep = document.RootElement.GetProperty("calls")[1].GetProperty("calls")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(suicide.Type, Is.EqualTo("suicide"));
            Assert.That(suicide.From, Is.EqualTo(victim));
            Assert.That(suicide.To, Is.EqualTo(Existing));
            Assert.That(suicide.Value, Is.EqualTo(VictimBalance));
            Assert.That(JsonSerializer.Serialize(parityTrace.StateChanges![victim], EthereumJsonSerializer.JsonOptions),
                Is.EqualTo("""{"balance":{"+":"0x0"},"code":{"+":""" + $"\"{victimCode.ToHexString(true)}\"" + """},"nonce":{"+":"0x1"},"storage":{"0x0000000000000000000000000000000000000000000000000000000000000000":{"+":"0x000000000000000000000000000000000000000000000000000000000000c0de"}}}"""),
                "the swept account is born and kept");
            Assert.That(sweep.GetProperty("type").GetString(), Is.EqualTo("SELFDESTRUCT"));
            Assert.That(sweep.GetProperty("from").GetString(), Is.EqualTo(victim.ToString()));
            Assert.That(sweep.GetProperty("to").GetString(), Is.EqualTo(Existing.ToString()));
            Assert.That(sweep.GetProperty("value").GetString(), Is.EqualTo("0x1234"));
        }
    }

    [Test]
    public void Prestate_diff_keeps_a_same_transaction_swept_account(
        [Values(Instruction.CREATE, Instruction.CREATE2)] Instruction createOpcode,
        [Values] bool enabled)
    {
        byte[] victimCode = SstoreCanaryThenSendAll(Existing);
        byte[] init = InitOf(victimCode);
        Address victim = CreatedAddress(Factory, createOpcode, init);
        InstallCode(Factory, Prepare.EvmCode
            .Data(CreateAndStore(createOpcode, init, VictimBalance, slot: 0))
            .Data(CallAndStore(victim, slot: 1))
            .STOP()
            .Done, VictimBalance);
        // EIP-8246's surviving-account fallback would hide an erroneous tracer deletion marker.
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip4758Enabled = enabled, IsEip8246Enabled = false };
        Transaction tx = BuildTx(Factory);
        Block block = BuildBlock(tx);
        GethTraceOptions options = GethTraceOptions.Default with
        {
            Tracer = NativePrestateTracer.PrestateTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"diffMode":true}""")
        };
        using NativePrestateTracer tracer = new(TestState, options, tx.Hash, Sender, Factory, block.Beneficiary, tx, spec);

        Execute(tx, spec, tracer);

        using GethLikeTxTrace trace = tracer.BuildResult();
        NativePrestateTracerDiffMode diff = (NativePrestateTracerDiffMode)trace.CustomTracerResult!.Value;
        Assert.That(diff.post.TryGetValue(victim, out NativePrestateTracerAccount? victimPost), Is.EqualTo(enabled));
        Assert.That(diff.post.TryGetValue(Existing, out NativePrestateTracerAccount? beneficiaryPost), Is.True);
        using (Assert.EnterMultipleScope())
        {
            if (enabled)
            {
                Assert.That(victimPost!.Nonce, Is.EqualTo((UInt256?)UInt256.One));
                Assert.That(victimPost.Code.ToArray(), Is.EqualTo(victimCode));
                Assert.That(victimPost.Storage![UInt256.Zero], Is.EqualTo(Canary));
            }
            Assert.That(beneficiaryPost!.Balance, Is.EqualTo((UInt256?)(1 + VictimBalance)));
        }
    }

    private GasConsumed RunCreateAndSendAll(Address factory, Address? fresh, BeneficiaryKind kind, IReleaseSpec spec, UInt256? value = null)
    {
        Address? target = kind switch { BeneficiaryKind.Fresh => fresh, BeneficiaryKind.Existing => Existing, _ => null };
        byte[] init = InitOf(SstoreCanaryThenSendAll(target));
        Address victim = CreatedAddress(factory, Instruction.CREATE, init);
        UInt256 endowment = value ?? VictimBalance;
        InstallCode(factory, Prepare.EvmCode
            .Data(CreateAndStore(Instruction.CREATE, init, endowment, slot: 0))
            .Data(CallAndStore(victim, slot: 1))
            .STOP()
            .Done, endowment);

        TestAllTracerWithOutput tracer = Run(factory, spec);
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        return tracer.GasConsumedResult;
    }

    /// <summary>SSTORE(0, <see cref="Canary"/>), then SELFDESTRUCT to <paramref name="target"/>, or to itself when null.</summary>
    private static byte[] SstoreCanaryThenSendAll(Address? target)
    {
        Prepare code = Prepare.EvmCode.PushData(Canary).PushData(0).Op(Instruction.SSTORE);
        return (target is null ? code.ADDRESS().Op(Instruction.SELFDESTRUCT) : code.SELFDESTRUCT(target)).Done;
    }

    private static byte[] InitOf(byte[] runtime) => Prepare.EvmCode.ForInitOf(runtime).Done;

    private static Address CreatedAddress(Address factory, Instruction createOpcode, byte[] init) =>
        createOpcode == Instruction.CREATE2 ? ContractAddress.From(factory, Salt, init) : ContractAddress.From(factory, 1);

    private static byte[] CreateAndStore(Instruction createOpcode, byte[] init, in UInt256 value, int slot) =>
        (createOpcode == Instruction.CREATE2 ? Prepare.EvmCode.Create2(init, Salt, value) : Prepare.EvmCode.Create(init, value))
            .PushData(slot)
            .Op(Instruction.SSTORE)
            .Done;

    /// <summary>CREATE2 with <see cref="VictimBalance"/>, storing the result at the slot named by the first calldata word.</summary>
    private static byte[] Create2AndStoreAtCalldataSlot(byte[] init) =>
        Prepare.EvmCode
            .Create2(init, Salt, VictimBalance)
            .PushData(0)
            .Op(Instruction.CALLDATALOAD)
            .Op(Instruction.SSTORE)
            .Done;

    private static byte[] CallAndStore(Address target, int slot) =>
        Prepare.EvmCode.Call(target, CallGas).PushData(slot).Op(Instruction.SSTORE).Done;

    /// <summary>Calls the address on top of the stack and drops the result.</summary>
    private static byte[] CallStackTarget() =>
        Prepare.EvmCode
            .PushData(0).Op(Instruction.SWAP1)
            .PushData(0).Op(Instruction.SWAP1)
            .PushData(0).Op(Instruction.SWAP1)
            .PushData(0).Op(Instruction.SWAP1)
            .PushData(0).Op(Instruction.SWAP1)
            .PushData(CallGas)
            .Op(Instruction.CALL)
            .Op(Instruction.POP)
            .Done;

    /// <summary>Calls <see cref="Factory"/> once per slot, each in its own frame so an EIP-684 collision burns only that frame's gas.</summary>
    private static byte[] CallFactoryForSlots(params int[] slots)
    {
        Prepare code = Prepare.EvmCode;
        foreach (int slot in slots)
        {
            code.CallWithInput(Factory, FactoryCallGas, new UInt256((ulong)slot).ToBigEndian()).Op(Instruction.POP);
        }

        return code.STOP().Done;
    }

    private void SetCanarySlots(Address address, int count)
    {
        for (int slot = 0; slot < count; slot++)
        {
            TestState.Set(new StorageCell(address, (UInt256)slot), SlotCanary);
        }

        TestState.Commit(Spec);
    }

    private void InstallCode(Address address, byte[] code, in UInt256 balance = default)
    {
        TestState.CreateAccount(address, balance, 1);
        TestState.InsertCode(address, code, Spec);
        TestState.Commit(Spec);
    }

    private Transaction BuildTx(Address to) =>
        Build.A.Transaction.WithTo(to).WithValue(0).WithGasLimit(GasLimit).WithNonce(TestState.GetNonce(Sender))
            .SignedAndResolved(_ecdsa, SenderKey).TestObject;

    private Block BuildBlock(Transaction tx, ulong? timestamp = null) =>
        Build.A.Block.WithNumber(BlockNumber).WithTimestamp(timestamp ?? Timestamp).WithGasLimit(2 * GasLimit)
            .WithTransactions(tx).TestObject;

    private TestAllTracerWithOutput Run(Address to, IReleaseSpec spec, ulong? timestamp = null)
    {
        // Access tracing warms every touched account, which would hide cold-access charges.
        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = false;
        Execute(BuildTx(to), spec, tracer, timestamp: timestamp);
        return tracer;
    }

    private TestAllTracerWithOutput Execute(Transaction tx, IReleaseSpec spec)
    {
        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = false;
        Execute(tx, spec, tracer);
        return tracer;
    }

    private void Execute(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ITransactionProcessor? processor = null, ulong? timestamp = null)
    {
        Block block = BuildBlock(tx, timestamp);
        (processor ?? _processor).Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);
        TestState.Commit(spec);
    }

    private UInt256 Storage(Address address, int slot)
    {
        TestState.Get(new StorageCell(address, (UInt256)slot), out UInt256 value);
        return value;
    }

    private static UInt256 ToWord(Address address) => new(address.Bytes, isBigEndian: true);

    private void AssertPersisted(Address address, byte[] code, in UInt256 balance, UInt256? slot0 = null)
    {
        Assert.That(TestState.AccountExists(address), Is.True, "the account is never destroyed");
        Assert.That(TestState.GetNonce(address), Is.EqualTo(1UL), "nonce kept");
        Assert.That(TestState.GetCodeHash(address), Is.EqualTo(Keccak.Compute(code).ValueHash256), "code kept");
        Assert.That(TestState.GetBalance(address), Is.EqualTo(balance));
        if (slot0 is not null)
        {
            Assert.That(Storage(address, 0), Is.EqualTo(slot0.Value), "storage kept");
        }
    }

    private void AssertBalanceOnly(Address address, in UInt256 balance)
    {
        Assert.That(TestState.AccountExists(address), Is.True);
        Assert.That(TestState.GetBalance(address), Is.EqualTo(balance));
        Assert.That(TestState.GetNonce(address), Is.EqualTo(0UL));
        Assert.That(TestState.IsContract(address), Is.False);
        Assert.That(Storage(address, 0), Is.EqualTo(UInt256.Zero));
    }

    private sealed class LogTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public byte StatusCode { get; private set; }
        public LogEntry[] Logs { get; private set; } = [];

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            StatusCode = Evm.StatusCode.Success;
            Logs = logs;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) =>
            StatusCode = Evm.StatusCode.Failure;
    }
}
