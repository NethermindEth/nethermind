// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// An address that dispatches to a precompile may still hold code in state: a genesis allocation, a precompile
/// moved by a state override onto a contract, or code an override left at a precompile's address in an earlier
/// simulated block. EXTCODESIZE, EXTCODECOPY and EXTCODEHASH report that code, as geth and the execution specs do,
/// whichever dispatch table runs and whether or not EXTCODESIZE fuses with the comparison after it, while a call to
/// the address still runs the precompile.
/// </summary>
/// <remarks>
/// Each run takes its transaction processor from a processing scope of the production modules over
/// <see cref="VirtualMachineTestsBase.TestState"/>, decorated as the env a scenario stands for.
/// </remarks>
[Parallelizable(ParallelScope.Self)]
public class ExtCodeAtPrecompileAddressTests : VirtualMachineTestsBase
{
    public enum Form { Size, SizeIsZero, SizeGtZero, SizeEqZero, SizeGtOne, Copy, Hash, CallReturnSize }
    public enum Dispatch { Traced, Untraced, UntracedCancelable }
    public enum Target { IdentityWithCode, P256VerifyWithCode, IdentityWithoutCode, Contract }
    public enum Fork { Byzantium, Berlin, Osaka, Amsterdam }

    private const int CallInputLength = 5;

    /// <summary>Returns a 32-byte word, so a call that ran it instead of the precompile is told apart.</summary>
    private static readonly byte[] TargetCode = Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;

    private static readonly Address ContractAddress = TestItem.AddressC;
    private static readonly Address MoveDestination = new("0x2000000000000000000000000000000000000002");

    private IContainer _container = null!;
    private ILifetimeScope? _processingScope;

    [OneTimeSetUp]
    public void BuildContainer() => _container = new ContainerBuilder()
        .AddModule(new TestNethermindModule())
        .AddSingleton(SpecProvider)
        .Build();

    [OneTimeTearDown]
    public void DisposeContainer() => _container.Dispose();

    [TearDown]
    public override void TearDown()
    {
        _processingScope?.Dispose();
        _processingScope = null;
        base.TearDown();
    }

    private static IEnumerable<TestCaseData> GenesisCases()
    {
        foreach (Fork fork in Enum.GetValues<Fork>())
            foreach (Target target in Enum.GetValues<Target>())
                foreach (Form form in Enum.GetValues<Form>())
                {
                    // EXTCODEHASH arrives with Constantinople.
                    if (form == Form.Hash && fork == Fork.Byzantium) continue;
                    foreach (Dispatch dispatch in Enum.GetValues<Dispatch>())
                        yield return new TestCaseData(fork, target, form, dispatch);
                }
    }

    private static IEnumerable<TestCaseData> OverrideCases()
    {
        foreach (Form form in Enum.GetValues<Form>())
            foreach (Dispatch dispatch in Enum.GetValues<Dispatch>())
                yield return new TestCaseData(form, dispatch);
    }

    [TestCaseSource(nameof(GenesisCases))]
    public void Account_in_state_is_reported_by_every_form(Fork fork, Target target, Form form, Dispatch dispatch)
    {
        ForkActivation activation = ActivationOf(fork);
        IReleaseSpec spec = SpecProvider.GetSpec(activation);
        Address address = AddressOf(target);
        byte[] code = target == Target.IdentityWithoutCode ? [] : TargetCode;
        TestState.CreateAccount(address, 1);
        if (code.Length != 0) TestState.InsertCode(address, code, spec);

        bool isPrecompile = spec.IsPrecompile(address);
        Assert.That(isPrecompile, Is.EqualTo(target != Target.Contract && (target != Target.P256VerifyWithCode || fork >= Fork.Osaka)), "precompile at this fork");

        AssertForm(Processor(), activation, address, code, form, dispatch, isPrecompile ? PrecompileOutputLength(address) : null);
    }

    /// <summary>A precompile moved onto a contract runs there, but the contract keeps its code.</summary>
    [TestCaseSource(nameof(OverrideCases))]
    public void Move_destination_of_a_precompile_reports_its_code(Form form, Dispatch dispatch)
    {
        ForkActivation activation = MainnetSpecProvider.OsakaActivation;
        IReleaseSpec spec = SpecProvider.GetSpec(activation);
        TestState.CreateAccount(MoveDestination, 1);
        TestState.InsertCode(MoveDestination, TargetCode, spec);
        (ITransactionProcessor processor, IOverridableCodeInfoRepository repository) = OverridingProcessor();
        TestState.ApplyStateOverridesNoCommit(repository,
            new Dictionary<Address, AccountOverride> { [PrecompiledAddresses.Identity] = new() { MovePrecompileToAddress = MoveDestination } }, spec);

        AssertForm(processor, activation, MoveDestination, TargetCode, form, dispatch, CallInputLength);
    }

    /// <summary>
    /// Each simulated block dispatches the spec's precompiles again, so code an override put at a precompile's
    /// address in an earlier block stays in state while the precompile runs there.
    /// </summary>
    [TestCaseSource(nameof(OverrideCases))]
    public void Code_left_at_a_precompile_address_by_an_earlier_block_override_is_reported(Form form, Dispatch dispatch)
    {
        ForkActivation activation = MainnetSpecProvider.OsakaActivation;
        IReleaseSpec spec = SpecProvider.GetSpec(activation);
        (ITransactionProcessor processor, IOverridableCodeInfoRepository repository) = OverridingProcessor();
        TestState.ApplyStateOverridesNoCommit(repository,
            new Dictionary<Address, AccountOverride> { [PrecompiledAddresses.Identity] = new() { Code = TargetCode } }, spec);
        TestState.Commit(spec);
        // The next block, without overrides of its own.
        TestState.ApplyStateOverridesNoCommit(repository, null, spec);

        AssertForm(processor, activation, PrecompiledAddresses.Identity, TargetCode, form, dispatch, CallInputLength);
    }

    /// <summary>
    /// A block access list state serves code by hash only when the block declares it, so code from the parent state is
    /// read by the account's address, as the code resolver does for any account.
    /// </summary>
    [Test]
    public void Code_at_a_precompile_address_from_the_parent_of_a_block_access_list_state_is_reported(
        [Values(Form.Size, Form.Copy)] Form form, [Values] Dispatch dispatch)
    {
        ForkActivation activation = MainnetSpecProvider.AmsterdamActivation;
        Address address = PrecompiledAddresses.Identity;
        TestState.CreateAccount(address, 1);
        TestState.InsertCode(address, TargetCode, SpecProvider.GetSpec(activation));
        // Every account the transaction touches, declared without a change, so each is read from the parent state.
        ReadOnlyBlockAccessList blockAccessList = Build.A.BlockAccessList
            .WithAccountChanges([.. new[] { Sender, Recipient, Miner, address }.Select(static account => Build.An.AccountChanges.WithAddress(account).TestObject)])
            .TestObject;
        ITransactionProcessor processor = Processor(static builder => builder.AddDecorator<IWorldState, BlockAccessListBasedWorldState>());
        BlockAccessListBasedWorldState state = (BlockAccessListBasedWorldState)_processingScope!.Resolve<IWorldState>();
        state.SetBlockAccessIndex(1);
        state.Setup(Build.A.Block.WithBlockAccessList(blockAccessList).TestObject);
        state.SetParentReader(TestState);

        AssertForm(processor, activation, address, TargetCode, form, dispatch, CallInputLength);
    }

    /// <summary>
    /// The code at a precompile's address is read only when the account's code hash says there is some: a precompile
    /// without code, the mainnet case, loads nothing, while code that is there is read through the bytecode access a
    /// witness records.
    /// </summary>
    [Test]
    public void Code_at_a_precompile_address_is_read_only_when_there_is_some([Values(Form.Size, Form.Copy)] Form form, [Values] bool hasCode)
    {
        ForkActivation activation = MainnetSpecProvider.OsakaActivation;
        Address address = PrecompiledAddresses.Identity;
        byte[] code = hasCode ? TargetCode : [];
        TestState.CreateAccount(address, 1);
        if (hasCode) TestState.InsertCode(address, code, SpecProvider.GetSpec(activation));
        ITransactionProcessor processor = Processor(static builder => builder.AddDecorator<IWorldState, CodeReadCountingWorldState>());
        CodeReadCountingWorldState state = (CodeReadCountingWorldState)_processingScope!.Resolve<IWorldState>();

        AssertForm(processor, activation, address, code, form, Dispatch.Untraced, CallInputLength);
        Assert.That(state.CodeReads, hasCode ? Is.Positive : Is.Zero, "reads of the code at the precompile's address");
    }

    /// <summary>The transaction processor of a processing scope over the test state, wired further by <paramref name="configure"/>.</summary>
    private ITransactionProcessor Processor(Action<ContainerBuilder>? configure = null)
    {
        _processingScope = _container.BeginLifetimeScope(builder =>
        {
            builder.AddSingleton(TestState);
            configure?.Invoke(builder);
        });
        return _processingScope.Resolve<ITransactionProcessor>();
    }

    /// <summary>A processor whose code repository takes state overrides, as the eth_call and eth_simulateV1 envs wire it.</summary>
    private (ITransactionProcessor Processor, IOverridableCodeInfoRepository Repository) OverridingProcessor()
    {
        ITransactionProcessor processor = Processor(static builder => builder
            .AddScoped<CodeOverrideStore>()
            .AddDecorator<ICodeInfoRepository, OverridableCodeInfoRepository>());
        return (processor, (OverridableCodeInfoRepository)_processingScope!.Resolve<ICodeInfoRepository>());
    }

    private void AssertForm(ITransactionProcessor processor, ForkActivation activation, Address address, byte[] code, Form form, Dispatch dispatch, int? precompileOutputLength)
    {
        (Block block, Transaction transaction) = PrepareTx(activation, 200_000, Observer(form, address));
        TestAllTracerWithOutput tracer = dispatch switch
        {
            Dispatch.Traced => new TestAllTracerWithOutput(),
            Dispatch.Untraced => new UntracedTracer(),
            _ => new UntracedCancelableTracer()
        };

        processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), tracer.Error);
            Assert.That(tracer.ReturnValue, Is.EqualTo(Expected(form, code, precompileOutputLength)), $"{form} of {address}");
        }
    }

    /// <summary>The 32-byte word geth returns for <paramref name="form"/> of an account holding <paramref name="code"/>.</summary>
    private static byte[] Expected(Form form, byte[] code, int? precompileOutputLength)
    {
        UInt256 size = (UInt256)code.Length;
        return form switch
        {
            Form.Size => size.ToBigEndian(),
            Form.SizeIsZero or Form.SizeEqZero => (code.Length == 0 ? UInt256.One : UInt256.Zero).ToBigEndian(),
            Form.SizeGtZero => (code.Length > 0 ? UInt256.One : UInt256.Zero).ToBigEndian(),
            Form.SizeGtOne => (code.Length > 1 ? UInt256.One : UInt256.Zero).ToBigEndian(),
            Form.Copy => code.PadRight(32),
            // Every target exists with a balance, so even one without code has the empty code's hash.
            Form.Hash => Keccak.Compute(code).Bytes.ToArray(),
            // A call runs the precompile when there is one, and the code otherwise.
            _ => ((UInt256)(precompileOutputLength ?? 32)).ToBigEndian(),
        };
    }

    /// <summary>Runs <paramref name="form"/> on <paramref name="address"/> and returns its result as one word.</summary>
    private static byte[] Observer(Form form, Address address)
    {
        Prepare prepare = Prepare.EvmCode;
        prepare = form switch
        {
            Form.Size => prepare.PushData(address).Op(Instruction.EXTCODESIZE),
            Form.SizeIsZero => prepare.PushData(address).Op(Instruction.EXTCODESIZE).Op(Instruction.ISZERO),
            Form.SizeGtZero => prepare.PushData(0).PushData(address).Op(Instruction.EXTCODESIZE).Op(Instruction.GT),
            Form.SizeEqZero => prepare.PushData(0).PushData(address).Op(Instruction.EXTCODESIZE).Op(Instruction.EQ),
            // A nonzero comparand is not a contract check, so this compare does not fuse.
            Form.SizeGtOne => prepare.PushData(1).PushData(address).Op(Instruction.EXTCODESIZE).Op(Instruction.GT),
            Form.Copy => prepare.PushData(32).PushData(0).PushData(0).PushData(address).Op(Instruction.EXTCODECOPY).PushData(0).Op(Instruction.MLOAD),
            Form.Hash => prepare.PushData(address).Op(Instruction.EXTCODEHASH),
            _ => prepare.STATICCALL(50_000, address, 0, CallInputLength, 0, 0).Op(Instruction.POP).Op(Instruction.RETURNDATASIZE),
        };
        return prepare.PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;
    }

    private static ForkActivation ActivationOf(Fork fork) => fork switch
    {
        Fork.Byzantium => new ForkActivation(MainnetSpecProvider.ByzantiumBlockNumber),
        Fork.Berlin => new ForkActivation(MainnetSpecProvider.BerlinBlockNumber),
        Fork.Osaka => MainnetSpecProvider.OsakaActivation,
        _ => MainnetSpecProvider.AmsterdamActivation,
    };

    private static Address AddressOf(Target target) => target switch
    {
        Target.IdentityWithCode or Target.IdentityWithoutCode => PrecompiledAddresses.Identity,
        Target.P256VerifyWithCode => PrecompiledAddresses.P256Verify,
        _ => ContractAddress,
    };

    /// <summary>Identity echoes its input; P256VERIFY answers an input of the wrong length with nothing.</summary>
    private static int PrecompileOutputLength(Address precompile) => precompile == PrecompiledAddresses.Identity ? CallInputLength : 0;

    private sealed class UntracedTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    private sealed class UntracedCancelableTracer : TestAllTracerWithOutput, ITxTracer
    {
        public override bool IsTracingInstructions => false;

        bool ITxTracer.IsCancelable => true;

        bool ITxTracer.IsCancelled => false;
    }

    /// <summary>Counts the reads of the identity precompile's account code, by its address or as a bytecode access recorded for it.</summary>
    private sealed class CodeReadCountingWorldState(IWorldState state) : WorldStateDecorator(state)
    {
        public int CodeReads { get; private set; }

        public override ReadOnlyMemory<byte> GetCode(Address address)
        {
            if (address == PrecompiledAddresses.Identity) CodeReads++;
            return base.GetCode(address);
        }

        public override void RecordBytecodeAccess(Address address)
        {
            if (address == PrecompiledAddresses.Identity) CodeReads++;
            base.RecordBytecodeAccess(address);
        }
    }
}
