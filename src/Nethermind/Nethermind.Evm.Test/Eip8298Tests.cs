// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
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
/// Tests for EIP-8298: SETCODEFROM code reuse instruction.
/// </summary>
public class Eip8298Tests : VirtualMachineTestsBase
{
    private static readonly Address Source = TestItem.AddressC;
    private static readonly byte[] SourceCode = Prepare.EvmCode.PushData(1).PushData(2).Op(Instruction.ADD).STOP().Done;
    private static readonly Hash256 SourceCodeHash = Keccak.Compute(SourceCode);
    private bool _eip8298Enabled = true;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.BogotaBlockTimestamp;
    protected override ISpecProvider SpecProvider => new TestSpecProvider(new OverridableReleaseSpec(Bogota.Instance) { IsEip8298Enabled = _eip8298Enabled });

    // Disable access tracing so cold/warm account access is charged per EIP-2929
    // (the default tracer pre-warms accesses, masking the cold cost in gas assertions).
    protected override TestAllTracerWithOutput CreateTracer() => new() { IsTracingAccess = false };

    // NUnit reuses the fixture instance, so a test that disables the EIP must not leak into the next one.
    public override void Setup()
    {
        _eip8298Enabled = true;
        base.Setup();
    }

    private void DeploySource(byte[]? code = null)
    {
        TestState.CreateAccount(Source, 1.Ether);
        TestState.InsertCode(Source, code ?? SourceCode, Spec);
    }

    [Test]
    public void ValidSource_AdoptsCodeHash_AndPushesOne()
    {
        DeploySource();
        byte[] code = Prepare.EvmCode.SETCODEFROM(Source).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.One));
        // Running frame keeps its loaded code, but the stored code hash now matches the source.
        AssertCodeHash(Recipient, Keccak.Compute(SourceCode));
    }

    private static object[] InvalidSourceCases =
    {
        new object[] { "empty", null!, false },
        new object[] { "eip7702-delegation", Bytes.Concat(Eip7702Constants.DelegationHeader, TestItem.AddressF.Bytes), true },
        new object[] { "eip3541-ef", new byte[] { 0xef, 0x00 }, true },
    };

    [TestCaseSource(nameof(InvalidSourceCases))]
    public void InvalidSource_PushesZero_AndLeavesCodeUnchanged(string name, byte[]? sourceCode, bool deploy)
    {
        if (deploy) DeploySource(sourceCode);
        byte[] code = Prepare.EvmCode.SETCODEFROM(Source).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), name);
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.Zero), name);
        AssertCodeHash(Recipient, Keccak.Compute(code));
    }

    [Test]
    public void PrecompileSource_PushesZero()
    {
        byte[] code = Prepare.EvmCode.SETCODEFROM(Sha256Precompile.Address).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.Zero));
    }

    // Validity is decided by the precompile address's state, not by it being a precompile.
    [TestCase(false, 1)]
    [TestCase(true, 0)]
    public void PrecompileSourceWithCodeInState_ValidatedByThatCode(bool efPrefixed, int expected)
    {
        byte[] precompileCode = efPrefixed ? [0xef, .. SourceCode] : SourceCode;
        TestState.CreateAccount(Sha256Precompile.Address, 1.Ether);
        TestState.InsertCode(Sha256Precompile.Address, precompileCode, Spec);
        byte[] code = Prepare.EvmCode.SETCODEFROM(Sha256Precompile.Address).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo((UInt256)expected));
        AssertCodeHash(Recipient, Keccak.Compute(expected == 1 ? precompileCode : code));
    }

    [Test]
    public void RevertAfterSetCodeFrom_RestoresCodeHash()
    {
        DeploySource();
        Address child = TestItem.AddressE;
        byte[] childCode = Prepare.EvmCode.SETCODEFROM(Source).POP().Revert(0, 0).Done;
        TestState.CreateAccount(child, 1.Ether);
        TestState.InsertCode(child, childCode, Spec);

        TestAllTracerWithOutput result = Execute(Prepare.EvmCode.Call(child, 100_000).MSTORE(0).Return(32, 0).Done);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.Zero), "child call reverted");
        AssertCodeHash(child, Keccak.Compute(childCode));
    }

    [Test]
    public void DelegateCall_UpdatesExecutingAccount_NotCodeSource()
    {
        DeploySource();
        Address library = TestItem.AddressE;
        byte[] libraryCode = Prepare.EvmCode.SETCODEFROM(Source).STOP().Done;
        TestState.CreateAccount(library, 1.Ether);
        TestState.InsertCode(library, libraryCode, Spec);

        TestAllTracerWithOutput result = Execute(Prepare.EvmCode.DelegateCall(library, 100_000).STOP().Done);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertCodeHash(Recipient, SourceCodeHash);
        AssertCodeHash(library, Keccak.Compute(libraryCode));
    }

    [Test]
    public void CurrentFrameKeepsLoadedCode_LaterSelfCallRunsAdoptedCode()
    {
        DeploySource(Prepare.EvmCode.PushData(42).MSTORE(0).Return(32, 0).Done);
        // Adopt, prove the frame continues by storing slot 1, then store what a CALL to self returns in slot 2.
        byte[] code = Prepare.EvmCode.SETCODEFROM(Source).POP()
            .PushData(0x11).PushData(1).Op(Instruction.SSTORE)
            .Call(Recipient, 100_000).POP()
            .PushData(32).PushData(0).PushData(0).Op(Instruction.RETURNDATACOPY)
            .PushData(0).Op(Instruction.MLOAD).PushData(2).Op(Instruction.SSTORE)
            .STOP().Done;

        TestAllTracerWithOutput result = Execute(Activation, 1_000_000, code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertStorage(1, (UInt256)0x11);
        AssertStorage(2, (UInt256)42);
    }

    [Test]
    public void StaticContext_ExceptionalHalt()
    {
        DeploySource();
        TestState.CreateAccount(TestItem.AddressD, 1.Ether);
        TestState.InsertCode(TestItem.AddressD, Prepare.EvmCode.SETCODEFROM(Source).STOP().Done, Spec);

        byte[] code = Prepare.EvmCode.StaticCall(TestItem.AddressD, 50000).MSTORE(0).Return(32, 0).Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        // The static inner frame halts exceptionally, so STATICCALL reports failure.
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo(UInt256.Zero));
    }

    public enum CreationKind { Transaction, Create, Create2 }

    public enum ReturnKind { ValidCode, EfPrefixed, Empty }

    [Test]
    public void Initcode_AdoptsCode_AndIgnoresReturnData([Values] CreationKind kind)
    {
        DeploySource();

        (TestAllTracerWithOutput result, Address created) = RunCreation(kind, AdoptingInitCode(0xef, 32), 0);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertCodeHash(created, SourceCodeHash);
        TestState.Get(new StorageCell(created, 0), out UInt256 observedHash);
        Assert.That(observedHash, Is.EqualTo(new UInt256(SourceCodeHash.Bytes, true)), "EXTCODEHASH seen by the initcode");
    }

    [Test]
    public void Initcode_AdoptedCode_ChargesNoCodeDeposit([Values] CreationKind kind)
    {
        DeploySource();

        (TestAllTracerWithOutput shortReturn, Address shortCreated) = RunCreation(kind, AdoptingInitCode(0x01, 1), 0);
        (TestAllTracerWithOutput longReturn, Address longCreated) = RunCreation(kind, AdoptingInitCode(0x01, 64), 1);

        AssertCodeHash(shortCreated, SourceCodeHash);
        AssertCodeHash(longCreated, SourceCodeHash);
        Assert.That(longReturn.GasSpent, Is.EqualTo(shortReturn.GasSpent));
    }

    [Test]
    public void Initcode_DelegateCallToSetCodeFrom_AdoptsForCreatedAccount([Values] CreationKind kind)
    {
        DeploySource();
        Address library = TestItem.AddressE;
        TestState.CreateAccount(library, 1.Ether);
        TestState.InsertCode(library, Prepare.EvmCode.SETCODEFROM(Source).STOP().Done, Spec);
        byte[] initCode = Prepare.EvmCode.DelegateCall(library, 100_000).POP()
            .PushData(0xef).PushData(0).Op(Instruction.MSTORE8).Return(32, 0).Done;

        (TestAllTracerWithOutput result, Address created) = RunCreation(kind, initCode, 0);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertCodeHash(created, SourceCodeHash);
    }

    [Test]
    public void Initcode_RevertAfterSetCodeFrom_LeavesNoCode([Values] CreationKind kind)
    {
        DeploySource();
        byte[] initCode = Prepare.EvmCode.SETCODEFROM(Source).POP().Revert(0, 0).Done;

        (_, Address created) = RunCreation(kind, initCode, 0);

        AssertCodeHash(created, Keccak.OfAnEmptyString);
    }

    [Test]
    public void Initcode_AdoptedCode_TracedAsSuccessfulCreation([Values] CreationKind kind)
    {
        DeploySource();

        ParityTraceAction creation = TraceCreation(kind, AdoptingInitCode(0xef, 32), 0);

        Assert.That(creation.Error, Is.Null);
        Assert.That(creation.Result!.Code, Is.EqualTo(SourceCode));
    }

    [Test]
    public void Creation_WithoutSetCodeFrom_TracedTheSameWithEip8298Off([Values] CreationKind kind)
    {
        byte[] initCode = Prepare.EvmCode.ForInitOf(SourceCode).Done;

        ParityTraceAction enabled = TraceCreation(kind, initCode, 0);
        _eip8298Enabled = false;
        ParityTraceAction disabled = TraceCreation(kind, initCode, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(enabled.Result!.Code, Is.EqualTo(SourceCode), "enabled code");
            Assert.That(disabled.Result!.Code, Is.EqualTo(SourceCode), "disabled code");
            Assert.That(enabled.Result.GasUsed, Is.EqualTo(disabled.Result.GasUsed), "gas");
        }
    }

    private ParityTraceAction TraceCreation(CreationKind kind, byte[] initCode, int run)
    {
        (Block block, Transaction tx, _) = PrepareCreation(kind, initCode, run);
        ParityLikeTxTracer tracer = new(block, tx, ParityTraceTypes.Trace);

        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        ParityTraceAction action = tracer.BuildResult().Action!;
        return kind == CreationKind.Transaction ? action : action.Subtraces[0];
    }

    [Test]
    public void CallTracer_ReportsAdoptedCodeAsCreationOutput([Values(CreationKind.Create, CreationKind.Create2)] CreationKind kind)
    {
        DeploySource();
        (Block block, Transaction tx, _) = PrepareCreation(kind, AdoptingInitCode(0xef, 32), 0);
        using NativeCallTracer tracer = new(tx, SpecProvider.GetSpec(block.Header), GethTraceOptions.Default with { Tracer = NativeCallTracer.CallTracer });

        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using GethLikeTxTrace trace = tracer.BuildResult();
        NativeCallTracerCallFrame frame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value;
        NativeCallTracerCallFrame creation = frame.Calls[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(creation.Error, Is.Null, "error");
            Assert.That(creation.Output!.AsSpan().ToArray(), Is.EqualTo(SourceCode), "output");
        }
    }

    // A creation transaction reports the code it deploys: the callTracer root, the receipt output eth_call
    // returns, and the trace_* create result all agree. With EIP-8298 off, a plain creation, as on master.
    [Test]
    public void CreationTransaction_ReportsDeployedCodeAsOutput_InEveryTracer([Values] bool eip8298Enabled)
    {
        _eip8298Enabled = eip8298Enabled;
        DeploySource();
        byte[] initCode = eip8298Enabled ? AdoptingInitCode(0xef, 32) : Prepare.EvmCode.ForInitOf(SourceCode).Done;
        (Block block, Transaction tx, Address created) = PrepareCreation(CreationKind.Transaction, initCode, 0);
        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        using NativeCallTracer callTracer = new(tx, spec, GethTraceOptions.Default with { Tracer = NativeCallTracer.CallTracer });
        ParityLikeTxTracer parityTracer = new(block, tx, ParityTraceTypes.Trace);
        TestAllTracerWithOutput receiptTracer = CreateTracer();

        _processor.Execute(tx, new BlockExecutionContext(block.Header, spec), new CompositeTxTracer(callTracer, parityTracer, receiptTracer));

        using GethLikeTxTrace trace = callTracer.BuildResult();
        NativeCallTracerCallFrame root = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TestState.GetCode(created).ToArray(), Is.EqualTo(SourceCode), "deployed code");
            Assert.That(root.Output!.AsSpan().ToArray(), Is.EqualTo(SourceCode), "callTracer root output");
            Assert.That(receiptTracer.ReturnValue, Is.EqualTo(SourceCode), "eth_call output");
            Assert.That(parityTracer.BuildResult().Action!.Result!.Code, Is.EqualTo(SourceCode), "trace_* created code");
        }
    }

    [Test]
    public void PrestateTracer_IncludesSourceAndAdoptedCode([Values] bool eip8298Enabled, [Values] bool diffMode)
    {
        _eip8298Enabled = eip8298Enabled;
        DeploySource();
        (Block block, Transaction tx) = PrepareTx(Activation, 1_000_000, Prepare.EvmCode.SETCODEFROM(Source).STOP().Done);
        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        GethTraceOptions options = GethTraceOptions.Default with
        {
            Tracer = NativePrestateTracer.PrestateTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>(diffMode ? """{"diffMode":true}""" : "{}")
        };
        GethLikeNativeTxTracer tracer = GethLikeNativeTracerFactory.CreateTracer(options, block, tx, TestState, spec);

        _processor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);

        using GethLikeTxTrace trace = tracer.BuildResult();
        if (diffMode)
        {
            Dictionary<AddressAsKey, NativePrestateTracerAccount> post = ((NativePrestateTracerDiffMode)trace.CustomTracerResult!.Value).post;
            Assert.That(post.TryGetValue(Recipient, out NativePrestateTracerAccount? recipient) ? recipient.Code.ToArray() : null,
                eip8298Enabled ? Is.EqualTo(SourceCode) : Is.Null.Or.Empty, "adopted code in post state");
            return;
        }

        Dictionary<AddressAsKey, NativePrestateTracerAccount> prestate = (Dictionary<AddressAsKey, NativePrestateTracerAccount>)trace.CustomTracerResult!.Value;
        Assert.That(prestate.TryGetValue(Source, out NativePrestateTracerAccount? source), Is.EqualTo(eip8298Enabled), "source in prestate");
        if (eip8298Enabled) Assert.That(source!.Code.ToArray(), Is.EqualTo(SourceCode), "source code");
    }

    [Test]
    public void ParityStateDiff_ReportsAdoptedCode([Values] bool eip8298Enabled)
    {
        _eip8298Enabled = eip8298Enabled;
        DeploySource();
        (Block block, Transaction tx) = PrepareTx(Activation, 1_000_000, Prepare.EvmCode.SETCODEFROM(Source).STOP().Done);
        ParityLikeTxTracer tracer = new(block, tx, ParityTraceTypes.StateDiff);

        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Dictionary<Address, ParityAccountStateChange> changes = tracer.BuildResult().StateChanges!;
        Assert.That(changes.TryGetValue(Recipient, out ParityAccountStateChange? recipient) ? recipient.Code?.After : null,
            eip8298Enabled ? Is.EqualTo(SourceCode) : Is.Null);
    }

    [Test]
    public void BlockAccessList_RecordsSourceReadAndCodeChange([Values] bool eip8298Enabled, [Values] bool parallel)
    {
        _eip8298Enabled = eip8298Enabled;
        DeploySource();
        TracedAccessWorldState tracedState = new(TestState, parallel);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, SpecProvider, tracedState, Machine,
            new EthereumCodeInfoRepository(tracedState), LimboLogs.Instance);
        (Block block, Transaction tx) = PrepareTx(Activation, 1_000_000, Prepare.EvmCode.SETCODEFROM(Source).STOP().Done);

        processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        BlockAccessListAtIndex accessList = tracedState.GetGeneratingBlockAccessList()!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accessList.GetAccountChanges(Source), eip8298Enabled ? Is.Not.Null : Is.Null, "source read");
            Assert.That(accessList.GetAccountChanges(Recipient)?.CodeChange?.Code, eip8298Enabled ? Is.EqualTo(SourceCode) : Is.Null, "code change");
        }
    }

    // Creation completion only consults EIP-8298 once SETCODEFROM has run, so plain creations must not change.
    [Test]
    public void Creation_WithoutSetCodeFrom_IsUnaffectedByEip8298([Values] CreationKind kind, [Values] ReturnKind returnKind)
    {
        byte[] initCode = returnKind switch
        {
            ReturnKind.ValidCode => Prepare.EvmCode.ForInitOf(SourceCode).Done,
            ReturnKind.EfPrefixed => Prepare.EvmCode.ForInitOf([0xef, .. SourceCode]).Done,
            _ => Prepare.EvmCode.STOP().Done,
        };

        (TestAllTracerWithOutput enabled, Address enabledCreated) = RunCreation(kind, initCode, 0);
        _eip8298Enabled = false;
        (TestAllTracerWithOutput disabled, Address disabledCreated) = RunCreation(kind, initCode, 1);

        Assert.That(TestState.GetCodeHash(enabledCreated), Is.EqualTo(TestState.GetCodeHash(disabledCreated)), "code hash");
        Assert.That(enabled.StatusCode, Is.EqualTo(disabled.StatusCode), "status");
        Assert.That(enabled.GasSpent, Is.EqualTo(disabled.GasSpent), "gas");
        AssertCodeHash(enabledCreated, returnKind == ReturnKind.ValidCode ? SourceCodeHash : Keccak.OfAnEmptyString);
    }

    // Adopts the source's code, stores the EXTCODEHASH it then observes for itself, and returns data starting
    // with firstByte that creation would otherwise validate, install and charge code deposit for.
    private static byte[] AdoptingInitCode(byte firstByte, int returnSize) => Prepare.EvmCode
        .SETCODEFROM(Source).POP()
        .ADDRESS().Op(Instruction.EXTCODEHASH).PushData(0).Op(Instruction.SSTORE)
        .PushData(0).PushData(32).Op(Instruction.MSTORE)
        .PushData(firstByte).PushData(0).Op(Instruction.MSTORE8)
        .Return(returnSize, 0).Done;

    private (TestAllTracerWithOutput Result, Address Created) RunCreation(CreationKind kind, byte[] initCode, int run)
    {
        (Block block, Transaction tx, Address created) = PrepareCreation(kind, initCode, run);
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return (tracer, created);
    }

    /// <summary>Prepares a run of <paramref name="initCode"/> through <paramref name="kind"/>; distinct runs create distinct accounts.</summary>
    private (Block Block, Transaction Tx, Address Created) PrepareCreation(CreationKind kind, byte[] initCode, int run)
    {
        if (kind == CreationKind.Transaction)
        {
            SenderRecipientAndMiner accounts = new() { SenderKey = run == 0 ? TestItem.PrivateKeyA : TestItem.PrivateKeyF };
            (Block block, Transaction tx) = PrepareInitTx(Activation, 1_000_000, initCode, accounts);
            return (block, tx, ContractAddress.From(accounts.Sender, 0));
        }

        byte[] salt = new byte[32];
        salt[^1] = (byte)(run + 1);
        UInt256 nonce = TestState.AccountExists(Recipient) ? TestState.GetNonce(Recipient) : UInt256.Zero;
        Address created = kind == CreationKind.Create
            ? ContractAddress.From(Recipient, nonce)
            : ContractAddress.From(Recipient, salt, initCode);
        byte[] factory = kind == CreationKind.Create
            ? Prepare.EvmCode.Create(initCode, 0).STOP().Done
            : Prepare.EvmCode.Create2(initCode, salt, 0).STOP().Done;
        (Block factoryBlock, Transaction factoryTx) = PrepareTx(Activation, 1_000_000, factory);
        return (factoryBlock, factoryTx, created);
    }

    [Test]
    public void SourceSelfDestructedInSameTx_AdoptedCodeRemainsAvailable()
    {
        // Runtime: self-destruct when called without calldata, otherwise return 42.
        byte[] selfDestruct = Prepare.EvmCode.SELFDESTRUCT(TestItem.AddressF).Done;
        const int jumpHeaderLength = 4; // CALLDATASIZE; PUSH1 dest; JUMPI
        byte[] runtime = Prepare.EvmCode.CALLDATASIZE().PushData(jumpHeaderLength + selfDestruct.Length).Op(Instruction.JUMPI)
            .Data(selfDestruct).JUMPDEST().PushData(42).MSTORE(0).Return(32, 0).Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtime).Done;
        byte[] salt = new byte[32];
        Address source = ContractAddress.From(Recipient, salt, initCode);
        Address adopter = TestItem.AddressE;
        TestState.CreateAccount(adopter, 1.Ether);
        TestState.InsertCode(adopter, Prepare.EvmCode.SETCODEFROM(source).STOP().Done, Spec);

        // Create the source, adopt its code, then self-destruct it, all in one transaction (EIP-6780).
        byte[] code = Prepare.EvmCode.Create2(initCode, salt, 0).POP()
            .Call(adopter, 100_000).POP()
            .Call(source, 100_000).POP()
            .STOP().Done;
        TestAllTracerWithOutput result = Execute(Activation, 1_000_000, code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(TestState.AccountExists(source), Is.False);
        ReadOnlyMemory<byte> adoptedCode = TestState.GetCode(adopter);
        Assert.That(adoptedCode.ToArray(), Is.EqualTo(runtime));

        result = Execute(Prepare.EvmCode.CallWithInput(adopter, 50_000, [1]).ReturnInnerCallResult().Done);

        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo((UInt256)42));
    }

    public enum SourceKind { Missing, SameCode, OtherCode }

    // Expected values are the EIP-8298 execution-gas table under the EIP-8038 access costs.
    [TestCase(SourceKind.Missing, false, 3100UL)]
    [TestCase(SourceKind.Missing, true, 200UL)]
    [TestCase(SourceKind.SameCode, false, 3200UL)]
    [TestCase(SourceKind.SameCode, true, 300UL)]
    [TestCase(SourceKind.OtherCode, false, 12200UL)]
    [TestCase(SourceKind.OtherCode, true, 9300UL)]
    public void Gas_MatchesSpecSchedule(SourceKind kind, bool warmSource, ulong expectedGas)
    {
        Prepare prefix = warmSource ? Prepare.EvmCode.EXTCODEHASH(Source).POP() : Prepare.EvmCode;
        // GAS; PUSH20 source; SETCODEFROM; POP; GAS; then return the difference between the two GAS readings.
        byte[] code = prefix.GAS().SETCODEFROM(Source).POP().GAS().SWAPx(1).SUB().MSTORE(0).Return(32, 0).Done;
        if (kind == SourceKind.SameCode) DeploySource(code);
        else if (kind == SourceKind.OtherCode) DeploySource();

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        const ulong measurementOverhead = GasCostOf.VeryLow + GasCostOf.Base + GasCostOf.Base;
        Assert.That(new UInt256(result.ReturnValue, true), Is.EqualTo((UInt256)(expectedGas + measurementOverhead)));
        AssertCodeHash(Recipient, Keccak.Compute(kind == SourceKind.OtherCode ? SourceCode : code));
    }

    public class Eip8298DisabledTests : VirtualMachineTestsBase
    {
        protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.BogotaBlockTimestamp;
        protected override ISpecProvider SpecProvider => new TestSpecProvider(Bogota.Instance);

        [Test]
        public void Opcode_WhenDisabled_Fails()
        {
            byte[] code = Prepare.EvmCode.SETCODEFROM(TestItem.AddressC).STOP().Done;
            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Failure));
        }
    }
}
