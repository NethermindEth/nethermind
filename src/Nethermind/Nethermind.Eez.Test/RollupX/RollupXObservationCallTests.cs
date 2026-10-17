// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXObservationCallTests
{
    private IWorldState _state = null!;
    private IDisposable _scope = null!;
    private ISpecProvider _specs = null!;
    private EthereumVirtualMachine _machine = null!;
    private EthereumCodeInfoRepository _code = null!;
    private BlockHeader _header = null!;
    private RollupXObservationCall _calls = null!;

    [SetUp]
    public void SetUp()
    {
        _specs = new TestSpecProvider(Osaka.Instance);
        _state = TestWorldStateFactory.CreateForTest();
        _scope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(TestItem.AddressA, 0, 7);
        _header = Build.A.Block.WithNumber(1).WithGasLimit(1_000_000).WithBaseFeePerGas(11)
            .WithBeneficiary(TestItem.AddressC).TestObject.Header;
        _header.GasUsed = 21_000;
        _machine = new EthereumVirtualMachine(new TestBlockhashProvider(_specs), _specs, LimboLogs.Instance);
        _machine.SetBlockExecutionContext(new BlockExecutionContext(_header, Osaka.Instance));
        _code = new EthereumCodeInfoRepository(_state);
        _machine.SetTxExecutionContext(new TxExecutionContext(TestItem.AddressD, _code, null, UInt256.One));
        _calls = new RollupXObservationCall(_specs, _state, _machine, _code, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    [Test]
    public void Execute_ReadOnlyCode_ReproducesCallerOriginAndInput()
    {
        Deploy([0x33, 0x5f, 0x52, 0x32, 0x60, 0x20, 0x52, 0x5f, 0x35, 0x60, 0x40, 0x52, 0x60, 0x60, 0x5f, 0xf3]);
        byte[] input = new byte[32];
        input[0] = 0x45;
        input[^1] = 0x78;
        byte[] expected = new byte[96];
        TestItem.AddressA.Bytes.CopyTo(expected.AsSpan(12));
        TestItem.AddressA.Bytes.CopyTo(expected.AsSpan(44));
        input.CopyTo(expected, 64);

        ObservationOutcome outcome = Execute(input, ObservationMode.Static);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Success, Is.True, "a static read executes with an unfunded contract caller and positive block base fee");
            Assert.That(outcome.Output, Is.EqualTo(expected), "the EVM sees the actual proxy caller, origin and calldata");
        }
        AssertRestored();
    }

    [TestCase((int)ObservationMode.Lookup, true, TestName = "LookupExecutesWriteThenRestores")]
    [TestCase((int)ObservationMode.Static, false, TestName = "StaticWriteHalts")]
    [TestCase((int)ObservationMode.Proxy, false, TestName = "ProxyDerivationWriteHalts")]
    public void Execute_StorageWrite_EnforcesCallModeAndRestoresState(int callMode, bool success)
    {
        Deploy([0x60, 42, 0x5f, 0x55, 0x00]);
        StorageCell cell = new(TestItem.AddressB, UInt256.Zero);
        _state.Set(in cell, UInt256.One);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);

        ObservationOutcome outcome = Execute([], (ObservationMode)callMode);

        Assert.That(outcome.Success, Is.EqualTo(success), "static execution must halt rather than merely discard a successful write");
        _state.Get(in cell, out UInt256 value);
        Assert.That(value, Is.EqualTo(UInt256.One), "the probe restores the exact persistent-storage value at its pin");
        AssertRestored();
    }

    [TestCase((int)ObservationMode.Lookup, TestName = "LookupRevertData")]
    [TestCase((int)ObservationMode.Static, TestName = "StaticRevertData")]
    public void Execute_Revert_ReturnsCompleteRevertData(int callMode)
    {
        Deploy([0x60, 0x45, 0x5f, 0x53, 0x60, 1, 0x5f, 0xfd]);

        ObservationOutcome outcome = Execute([], (ObservationMode)callMode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Success, Is.False, "a REVERT is a failed target call");
            Assert.That(outcome.Output, Is.EqualTo(new byte[] { 0x45 }), "failure preserves the full revert data");
        }
        AssertRestored();
    }

    [TestCase(0x55, TestName = "StaticRejectsPersistentStore")]
    [TestCase(0x5d, TestName = "StaticRejectsTransientStore")]
    [TestCase(0xa0, TestName = "StaticRejectsLog")]
    public void Execute_StaticMutation_RejectsForbiddenInstruction(byte opcode)
    {
        Deploy([0x5f, 0x5f, opcode, 0x00]);

        ObservationOutcome outcome = Execute([], ObservationMode.Static);

        Assert.That(outcome.Success, Is.False, "the EVM enforces its static instruction restrictions");
        AssertRestored();
    }

    [TestCase(0xf1, TestName = "UnsupportedNestedCallCannotBeCaught")]
    [TestCase(0xfa, TestName = "UnsupportedNestedStaticCallCannotBeCaught")]
    public void Execute_UnsupportedNestedCall_RejectsInsteadOfReturningClaimedSuccess(byte opcode)
    {
        byte[] arguments = opcode == 0xf1 ? new byte[] { 0x5f, 0x5f, 0x5f, 0x5f, 0x5f } : [0x5f, 0x5f, 0x5f, 0x5f];
        Deploy([.. arguments, 0x73, .. TestItem.AddressD.Bytes, 0x5a, opcode, 0x50, 0x5f, 0x5f, 0xf3]);

        Assert.That(() => Execute([], ObservationMode.Lookup), Throws.TypeOf<EezSettlementException>(),
            "a parent cannot catch an unsupported child call and turn it into an accepted observation");
        AssertRestored();
    }

    [Test]
    public void Execute_RepeatedMutableProbe_DoesNotRetainPreviousStorageOrNonce()
    {
        Deploy([0x5f, 0x54, 0x60, 1, 0x01, 0x80, 0x5f, 0x55, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3]);
        byte[] expected = new byte[32];
        expected[^1] = 1;

        ObservationOutcome first = Execute([], ObservationMode.Lookup);
        ObservationOutcome second = Execute([], ObservationMode.Lookup);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Success && second.Success, Is.True, "both mutable simulations execute");
            Assert.That(first.Output, Is.EqualTo(expected), "the first simulation observes the pinned storage");
            Assert.That(second.Output, Is.EqualTo(expected), "the second simulation starts from the same pinned storage");
        }
        AssertRestored();
    }

    [Test]
    public void Execute_ValueBearingCall_RejectsBeforeExecution()
    {
        Deploy([0x00]);
        Assert.That(() => _calls.ExecuteAtTransactionBoundary(TestItem.AddressA, TestItem.AddressB, ReadOnlyMemory<byte>.Empty,
            UInt256.One, ObservationMode.Lookup), Throws.TypeOf<EezSettlementException>(),
            "unimplemented balance-disabled value semantics cannot produce a proof");
        AssertRestored();
    }

    [Test]
    public void Execute_CancelledCall_DoesNotTouchState()
    {
        Deploy([0x00]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.That(() => _calls.ExecuteAtTransactionBoundary(TestItem.AddressA, TestItem.AddressB, ReadOnlyMemory<byte>.Empty,
            UInt256.Zero, ObservationMode.Static, cancellation.Token), Throws.TypeOf<OperationCanceledException>(),
            "cancelled work performs no EVM simulation");
        AssertRestored();
    }

    [TestCase((int)ObservationMode.Lookup, 29_978_998UL, TestName = "LookupUsesThirtyMillionTransactionGas")]
    [TestCase((int)ObservationMode.Static, 29_978_998UL, TestName = "StaticUsesThirtyMillionTransactionGas")]
    [TestCase((int)ObservationMode.Proxy, 978_998UL, TestName = "ProxyUsesOneMillionTransactionGas")]
    public void Execute_GasInstruction_UsesProfileBudget(int callMode, ulong gas)
    {
        Deploy([0x5a, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3]);
        byte[] expected = new byte[32];
        new UInt256(gas).ToBigEndian(expected);

        ObservationOutcome outcome = Execute([], (ObservationMode)callMode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Success, Is.True, "a successful view also exercises proxy-mode execution");
            Assert.That(outcome.Output, Is.EqualTo(expected), "GAS subtracts the 21,000 intrinsic transaction cost and its own two gas");
        }
        AssertRestored();
    }

    [Test]
    public void Execute_UnknownCallMode_RejectsBeforeExecution()
    {
        Deploy([0x00]);
        Assert.That(() => Execute([], (ObservationMode)99), Throws.TypeOf<EezSettlementException>(),
            "unknown modes cannot silently select mutable or static execution");
        AssertRestored();
    }

    [Test]
    public void Execute_StateGasFork_RejectsBeforeExecution()
    {
        Deploy([0x00]);
        _machine.SetBlockExecutionContext(new BlockExecutionContext(_header, new StateGasSpec()));
        Assert.That(() => Execute([], ObservationMode.Static), Throws.TypeOf<EezSettlementException>(),
            "unimplemented state-gas frame accounting cannot be accepted");
        AssertRestored();
    }

    [Test]
    public void Execute_NonemptyTransientJournal_RejectsWithoutClearingCallerState()
    {
        Deploy([0x00]);
        StorageCell cell = new(TestItem.AddressB, UInt256.Zero);
        _state.SetTransientState(in cell, UInt256.One);

        Assert.That(() => Execute([], ObservationMode.Static), Throws.TypeOf<EezSettlementException>(),
            "a probe must run between canonical transactions rather than clearing a caller's transient journal");

        _state.GetTransientState(in cell, out UInt256 value);
        Assert.That(value, Is.EqualTo(UInt256.One), "rejection preserves the active transaction's transient value");
        AssertRestored();
    }

    [TestCase(0xf4, TestName = "LocalDelegateCallRemainsSupported")]
    [TestCase(0xf2, TestName = "LocalCallCodeRemainsSupported")]
    public void Execute_LocalImplementationCall_ExecutesAtThePinnedState(byte opcode)
    {
        byte[] arguments = opcode == 0xf2 ? new byte[] { 0x5f, 0x5f, 0x5f, 0x5f, 0x5f } : [0x5f, 0x5f, 0x5f, 0x5f];
        Deploy([.. arguments, 0x73, .. TestItem.AddressD.Bytes, 0x5a, opcode, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3]);
        byte[] expected = new byte[32];
        expected[^1] = 1;

        ObservationOutcome outcome = Execute([], ObservationMode.Static);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Success, Is.True, "local implementation frames inherit static execution");
            Assert.That(outcome.Output, Is.EqualTo(expected), "local delegation is not treated as cross-chain dispatch");
        }
        AssertRestored();
    }

    [Test]
    public void Execute_PrecheckRejectedNestedCall_RejectsInsteadOfReturningSuccess()
    {
        Deploy([0x5f, 0x5f, 0x5f, 0x5f, 0x60, 1, 0x73, .. TestItem.AddressD.Bytes, 0x5a, 0xf1, 0x50, 0x00]);
        Assert.That(_state.GetBalance(TestItem.AddressB), Is.EqualTo(UInt256.Zero), "precondition: the child value transfer cannot pass the balance check");

        Assert.That(() => Execute([], ObservationMode.Lookup), Throws.TypeOf<EezSettlementException>(),
            "a rejected child CALL cannot bypass the unsupported-dispatch guard");
        AssertRestored();
    }

    [Test]
    public void Execute_CancelledDuringNonceSetup_StopsBeforeExecutingStorageWrite()
    {
        Deploy([0x60, 42, 0x5f, 0x55, 0x00]);
        using CancellationTokenSource cancellation = new();
        CancellingState state = new(_state, cancellation);
        RollupXObservationCall calls = new(_specs, state, _machine, _code, LimboLogs.Instance);

        Assert.That(() => calls.ExecuteAtTransactionBoundary(TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, UInt256.Zero, ObservationMode.Lookup, cancellation.Token),
            Throws.TypeOf<OperationCanceledException>(), "cancellation after setup interrupts the EVM before its first state write");

        Assert.That(state.Writes, Is.Zero, "checking cancellation only after execution would execute SSTORE before rollback");
        AssertRestored();
    }

    [Test]
    public void Execute_StateRestoreThrows_StillRestoresVirtualMachineContext()
    {
        Deploy([0x00]);
        RollupXObservationCall calls = new(_specs, new FailingRestoreState(_state), _machine, _code, LimboLogs.Instance);

        Assert.That(() => calls.ExecuteAtTransactionBoundary(TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, UInt256.Zero, ObservationMode.Static), Throws.TypeOf<InvalidOperationException>(),
            "a failed restore prevents a successful observation");
        AssertRestored();
    }

    [TestCase((int)ObservationMode.Lookup, true)]
    [TestCase((int)ObservationMode.Static, false)]
    public void VerifyClaim_StorageWrite_UsesClaimMode(int mode, bool success)
    {
        Deploy([0x60, 42, 0x5f, 0x55, 0x00]);
        DeployManager([0x73, .. TestItem.AddressA.Bytes, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3]);
        RollupXObservationClaim claim = new(Keccak.Zero, TestItem.AddressC, 0, TestItem.AddressB,
            [], UInt256.Zero, (ObservationMode)mode, new ObservationOutcome(success, []));

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing,
            "complete claim verification must distinguish static failure from successful mutable execution");
        Assert.That(() => _calls.VerifyClaim(claim with { Expected = new ObservationOutcome(!success, []) }, TestItem.AddressE, default),
            Throws.TypeOf<EezSettlementException>(), "a claimed status cannot override the actual EVM mode");
        AssertRestored();
    }

    [TestCase(0, false)]
    [TestCase(31, false)]
    [TestCase(33, false)]
    [TestCase(32, true)]
    public void VerifyClaim_NoncanonicalProxyResponse_Rejects(byte size, bool padding)
    {
        Deploy([0x00]);
        DeployManager([0x60, padding ? (byte)1 : (byte)0, 0x5f, 0x53, 0x60, size, 0x5f, 0xf3]);
        RollupXObservationClaim claim = new(Keccak.Zero, TestItem.AddressC, 0, TestItem.AddressB,
            [], UInt256.Zero, ObservationMode.Lookup, new ObservationOutcome(true, []));

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.TypeOf<EezSettlementException>(),
            "the proxy view must return exactly one canonical ABI address word");
        AssertRestored();
    }

    [Test]
    public void VerifyClaim_RevertingProxyResponse_Rejects()
    {
        Deploy([0x00]);
        DeployManager([0x73, .. TestItem.AddressA.Bytes, 0x5f, 0x52, 0x60, 32, 0x5f, 0xfd]);
        RollupXObservationClaim claim = new(Keccak.Zero, TestItem.AddressC, 0, TestItem.AddressB,
            [], UInt256.Zero, ObservationMode.Lookup, new ObservationOutcome(true, []));

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.TypeOf<EezSettlementException>());
        AssertRestored();
    }

    private void DeployManager(byte[] code)
    {
        _state.CreateAccount(TestItem.AddressE, 0);
        _state.InsertCode(TestItem.AddressE, code, Osaka.Instance);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);
    }

    private ObservationOutcome Execute(byte[] input, ObservationMode mode) =>
        _calls.ExecuteAtTransactionBoundary(TestItem.AddressA, TestItem.AddressB, input, UInt256.Zero, mode);

    private void Deploy(byte[] code)
    {
        _state.CreateAccount(TestItem.AddressB, 0);
        _state.InsertCode(TestItem.AddressB, code, Osaka.Instance);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);
        Assert.That(_state.GetNonce(TestItem.AddressA), Is.EqualTo(7), "precondition: the pinned caller has an existing nonce");
    }

    private void AssertRestored()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.GetNonce(TestItem.AddressA), Is.EqualTo(7), "probe nonce changes never alter canonical execution");
            Assert.That(_state.GetBalance(TestItem.AddressA), Is.EqualTo(UInt256.Zero), "the unfunded caller remains unfunded");
            Assert.That(_state.GetBalance(TestItem.AddressC), Is.EqualTo(UInt256.Zero), "simulation pays no beneficiary fee");
            Assert.That(_header.GasUsed, Is.EqualTo(21_000), "simulation consumes no canonical block gas");
            Assert.That(_machine.BlockExecutionContext.Header, Is.SameAs(_header), "the canonical block context remains installed");
            Assert.That(_machine.TxExecutionContext.Origin, Is.EqualTo(TestItem.AddressD.ToHash()), "the canonical transaction context is restored");
        }
    }

    private sealed class CancellingState(IWorldState state, CancellationTokenSource cancellation) : WorldStateDecorator(state)
    {
        public int Writes { get; private set; }

        public override ulong GetNonce(Address address)
        {
            ulong nonce = base.GetNonce(address);
            cancellation.Cancel();
            return nonce;
        }

        public override void Set(in StorageCell cell, in UInt256 value)
        {
            Writes++;
            base.Set(in cell, in value);
        }

        public override void Set(in StorageCell cell, in UInt256 value, in UInt256 currentValue)
        {
            Writes++;
            base.Set(in cell, in value, in currentValue);
        }
    }

    private sealed class FailingRestoreState(IWorldState state) : WorldStateDecorator(state)
    {
        public override void Restore(Snapshot snapshot)
        {
            base.Restore(snapshot);
            throw new InvalidOperationException("The restore failed.");
        }
    }

    private sealed class StateGasSpec() : ReleaseSpecDecorator(Osaka.Instance)
    {
        public override bool IsEip8037Enabled => true;
    }
}
