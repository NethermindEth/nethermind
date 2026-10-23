// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXObservationCallTests
{
    [TestCase(0xf1)]
    [TestCase(0xfa)]
    public void VerifyClaim_RegisteredStaticCallback_ReturnsAuthenticatedOutput(byte opcode)
    {
        Deploy([.. ProxyCall(opcode, 0x11, 0), 0x60, 32, 0x5f, 0xf3]);
        RegisterProxy();
        RollupXObservationClaim claim = CallbackClaim();

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing);
        AssertRestored();
    }

    [TestCase("caller")]
    [TestCase("target")]
    [TestCase("chain")]
    [TestCase("calldata")]
    [TestCase("missing")]
    [TestCase("extra")]
    [TestCase("output")]
    [TestCase("registration")]
    public void VerifyClaim_ChangedCallback_RejectsEvenWhenCallerCatchesFailure(string mutation)
    {
        Deploy([.. ProxyCall(0xfa, 0x11, 0), 0x60, 32, 0x5f, 0xf3]);
        RegisterProxy(mutation == "registration" ? (byte)2 : (byte)1);
        RollupXObservationClaim claim = CallbackClaim();
        RollupXObservationCallback callback = claim.Callbacks[0];
        claim = mutation switch
        {
            "caller" => claim with { Callbacks = [callback with { Source = TestItem.AddressA }] },
            "target" => claim with { Callbacks = [callback with { Target = TestItem.AddressA }] },
            "chain" => claim with { SourceRollupId = 1 },
            "calldata" => claim with { Callbacks = [callback with { Data = [0x12] }] },
            "missing" => claim with { Callbacks = [] },
            "extra" => claim with { Callbacks = [callback, callback] },
            "output" => claim with { Callbacks = [callback with { Output = Word(0x46) }] },
            _ => claim,
        };

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.TypeOf<EezSettlementException>());
        AssertRestored();
    }

    [Test]
    public void VerifyClaim_Callbacks_PreservesExactOrder([Values] bool reverse)
    {
        Deploy([.. ProxyCall(0xfa, 0x11, 0), .. ProxyCall(0xfa, 0x22, 32), 0x60, 64, 0x5f, 0xf3]);
        RegisterProxy();
        RollupXObservationClaim claim = CallbackClaim();
        RollupXObservationCallback first = claim.Callbacks[0];
        RollupXObservationCallback second = first with { Data = [0x22], Output = Word(0x78) };
        claim = claim with
        {
            Expected = new ObservationOutcome(true, [.. first.Output, .. second.Output]),
            Callbacks = reverse ? [second, first] : [first, second],
        };

        if (reverse) Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.TypeOf<EezSettlementException>());
        else Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing);
        AssertRestored();
    }

    [TestCase(0xf1)]
    [TestCase(0xfa)]
    public void VerifyClaim_UnregisteredLocalCall_ExecutesNormally(byte opcode)
    {
        Deploy([.. ProxyCall(opcode, 0x11, 0), 0x60, 32, 0x5f, 0xf3]);
        DeployManager([0x73, .. TestItem.AddressA.Bytes, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3]);
        _state.CreateAccount(TestItem.AddressD, 0);
        _state.InsertCode(TestItem.AddressD, new byte[] { 0x60, 0x45, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3 }, Osaka.Instance);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);
        RollupXObservationClaim claim = CallbackClaim() with { Callbacks = [] };

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing);
        AssertRestored();
    }

    [TestCase(0xf2)]
    [TestCase(0xf4)]
    public void VerifyClaim_RegisteredLocalImplementation_DoesNotConsumeCallback(byte opcode)
    {
        Deploy([.. ProxyCall(opcode, 0x11, 0), 0x60, 32, 0x5f, 0xf3]);
        RegisterProxy();
        _state.CreateAccount(TestItem.AddressD, 0);
        _state.InsertCode(TestItem.AddressD, new byte[] { 0x60, 0x45, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3 }, Osaka.Instance);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);
        RollupXObservationClaim claim = CallbackClaim() with { Callbacks = [] };

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing);
        AssertRestored();
    }

    [Test]
    public void VerifyClaim_RegisteredProxyCodeInspection_UsesActualCode()
    {
        Deploy([0x73, .. TestItem.AddressD.Bytes, 0x3b, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3]);
        RegisterProxy();
        _state.CreateAccount(TestItem.AddressD, 0);
        _state.InsertCode(TestItem.AddressD, new byte[] { 0x00 }, Osaka.Instance);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);
        RollupXObservationClaim claim = CallbackClaim() with { Callbacks = [], Expected = new(true, Word(1)) };

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing);
        AssertRestored();
    }

    [Test]
    public void Execute_ValueTransfer_PreservesSimulationBalanceAndRestores([Values(0, 5, 20)] int originalBalance)
    {
        Deploy([0x34, 0x5f, 0x52, 0x47, 0x60, 32, 0x52, 0x33, 0x31, 0x60, 64, 0x52, 0x60, 96, 0x5f, 0xf3]);
        _state.AddToBalance(TestItem.AddressA, (UInt256)(uint)originalBalance, Osaka.Instance, out _);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);

        ObservationOutcome outcome = _calls.ExecuteAtTransactionBoundary(TestItem.AddressA, TestItem.AddressB,
            ReadOnlyMemory<byte>.Empty, 7, ObservationMode.Lookup);

        Assert.That(outcome.Success, Is.True);
        byte[] expected = [.. Word(7), .. Word(7), .. Word((byte)Math.Max(0, originalBalance - 7))];
        Assert.That(outcome.Output, Is.EqualTo(expected));
        Assert.That(_state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)(uint)originalBalance));
        Assert.That(_state.GetBalance(TestItem.AddressB), Is.EqualTo(UInt256.Zero));
        Assert.That(_state.GetNonce(TestItem.AddressA), Is.EqualTo(7));
    }

    [Test]
    public void VerifyClaim_MissingProxyWitness_RejectsInsteadOfUsingOrdinaryCode()
    {
        Deploy([.. ProxyCall(0xfa, 0x11, 0), 0x60, 32, 0x5f, 0xf3]);
        RegisterProxy();
        RollupXObservationCall calls = new(_specs, new MissingProxyState(_state), _machine, _code, LimboLogs.Instance);
        RollupXObservationClaim claim = CallbackClaim() with { Callbacks = [], Expected = new(false, []) };

        Assert.That(() => calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.TypeOf<WitnessValidationException>());
        AssertRestored();
    }

    private sealed class MissingProxyState(IWorldState inner) : WorldStateDecorator(inner)
    {
        public override void Get(in StorageCell cell, out UInt256 value) =>
            throw new WitnessValidationException("Missing proxy mapping witness");
    }

    [Test]
    public void VerifyClaim_ProxyAuthorization_WarmsManagerAccount()
    {
        Deploy([0x5a, 0x5f, 0x52, 0x73, .. TestItem.AddressE.Bytes, 0x31, 0x50,
            0x5a, 0x60, 32, 0x52, 0x60, 64, 0x5f, 0xf3]);
        DeployManager([0x73, .. TestItem.AddressA.Bytes, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3]);
        RollupXObservationClaim claim = CallbackClaim() with
        {
            Callbacks = [],
            Expected = new(true, GasSamples(29_978_998, 29_978_883)),
        };

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing);
        AssertRestored();
    }

    [Test]
    public void VerifyClaim_ProxyAuthorization_WarmsStorageBeforeChildSnapshot([Values] bool revertedChild)
    {
        byte[] child = revertedChild
            ? [0x5f, 0x5f, 0x5f, 0x5f, 0x73, .. TestItem.AddressD.Bytes, 0x5a, 0xfa, 0x50] : [];
        Deploy([.. child, 0x60, 64, 0x5f, 0x5f, 0x5f, 0x73, .. TestItem.AddressE.Bytes,
            0x5a, 0xfa, 0x50, 0x60, 64, 0x5f, 0xf3]);
        byte[] key = new byte[64];
        (revertedChild ? TestItem.AddressD : TestItem.AddressB).Bytes.CopyTo(key.AsSpan(12));
        byte[] storageRead = [0x5a, 0x5f, 0x52, 0x7f, .. ValueKeccak.Compute(key).Bytes,
            0x54, 0x50, 0x5a, 0x60, 32, 0x52, 0x60, 64, 0x5f, 0xf3];
        byte[] manager = [0x36, 0x60, 68, 0x14, 0x60, (byte)(7 + storageRead.Length), 0x57, .. storageRead,
            0x5b, 0x73, .. TestItem.AddressA.Bytes, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3];
        DeployManager(manager);
        if (revertedChild)
        {
            _state.CreateAccount(TestItem.AddressD, 0);
            _state.InsertCode(TestItem.AddressD, new byte[] { 0x5f, 0x5f, 0xfd }, Osaka.Instance);
            _state.Commit(Osaka.Instance);
            _state.CommitTree(0);
        }
        ulong available = RollupXObservationCall.TargetGasLimit - 21_000 - (revertedChild ? 2619UL : 0) - 14 - 100 - 6;
        ulong firstSample = available - available / 64 - 23;
        RollupXObservationClaim claim = CallbackClaim() with
        {
            Callbacks = [],
            Expected = new(true, GasSamples(firstSample, firstSample - 115)),
        };

        Assert.That(() => _calls.VerifyClaim(claim, TestItem.AddressE, default), Throws.Nothing);
        AssertRestored();
    }

    private static byte[] GasSamples(ulong first, ulong second)
    {
        byte[] samples = new byte[64];
        new UInt256(first).ToBigEndian(samples.AsSpan(0, 32));
        new UInt256(second).ToBigEndian(samples.AsSpan(32));
        return samples;
    }

    private void RegisterProxy(byte registered = 1)
    {
        DeployManager([0x73, .. TestItem.AddressA.Bytes, 0x5f, 0x52, 0x60, 32, 0x5f, 0xf3]);
        byte[] key = new byte[64];
        TestItem.AddressD.Bytes.CopyTo(key.AsSpan(12));
        StorageCell cell = new(TestItem.AddressE, new UInt256(Keccak.Compute(key).Bytes, isBigEndian: true));
        byte[] packed = new byte[32];
        TestItem.AddressC.Bytes.CopyTo(packed.AsSpan(11));
        packed[31] = registered;
        _state.Set(in cell, new UInt256(packed, isBigEndian: true));
        _state.Commit(Osaka.Instance);
        _state.CommitTree(0);
    }

    private static RollupXObservationClaim CallbackClaim() => new(Keccak.Zero, TestItem.AddressC, 0, TestItem.AddressB,
        [], UInt256.Zero, ObservationMode.Static, new ObservationOutcome(true, Word(0x45)))
    {
        Callbacks = [new(TestItem.AddressB, TestItem.AddressC, [0x11], Word(0x45))],
    };

    private static byte[] ProxyCall(byte opcode, byte data, byte outputOffset) =>
        [0x60, data, 0x60, 96, 0x53, 0x60, 32, 0x60, outputOffset, 0x60, 1, 0x60, 96,
            .. (opcode is 0xf1 or 0xf2 ? new byte[] { 0x5f } : []),
            0x73, .. TestItem.AddressD.Bytes, 0x5a, opcode, 0x50];

    private static byte[] Word(byte value)
    {
        byte[] word = new byte[32];
        word[^1] = value;
        return word;
    }
}
