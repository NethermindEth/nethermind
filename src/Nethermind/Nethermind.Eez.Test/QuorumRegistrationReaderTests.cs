// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Proving;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class QuorumRegistrationReaderTests
{
    private const ulong RollupId = 2;
    private static readonly Address Registry = new("0x5fbdb2315678afecb367f032d93f642f64180aa3");
    private static readonly Address Manager = new("0x0d9fa77deab0cce9e49aa552b8c15e70fff473b3");
    private static readonly Address FirstSystem = new("0x00000000000000000000000000000000000000a1");
    private static readonly Address SecondSystem = new("0x00000000000000000000000000000000000000a2");
    private static readonly ValueHash256 FirstKey = Keccak.Compute("first key").ValueHash256;

    private IEezL1Api _l1 = null!;

    [SetUp]
    public void SetUp()
    {
        _l1 = Substitute.For<IEezL1Api>();
        Returns(Registry, "rollups(uint64)", [.. Word(Manager), .. new byte[64]]);
        ReturnsThreshold(1);
        Returns(Manager, "verificationKey(address)", FirstKey.ToByteArray(), FirstSystem);
        Returns(Manager, "verificationKey(address)", new byte[32], SecondSystem);
        Returns(FirstSystem, "signer()", Word(TestItem.AddressA));
        Returns(SecondSystem, "signer()", Word(TestItem.AddressB));
    }

    [Test]
    public async Task Read_TwoAttesters_KeysOnlyTheRegisteredOne()
    {
        QuorumRegistration registration = await Reader().Read([Attester(FirstSystem, TestItem.AddressA), Attester(SecondSystem, TestItem.AddressB)], CancellationToken.None);

        Assert.That((registration.Threshold, registration.VerificationKeys), Is.EqualTo((1, new[] { FirstKey, default })),
            "an attester whose proof system is not registered is not asked, since its proof would revert the batch");
    }

    [Test]
    public async Task Read_SignerRotated_AttesterIsNotAsked()
    {
        QuorumRegistration registration = await Reader().Read([Attester(FirstSystem, TestItem.AddressC)], CancellationToken.None);

        Assert.That(registration.VerificationKeys, Is.EqualTo(new[] { default(ValueHash256) }), "a proof by a signer the proof system no longer accepts reverts");
    }

    [TestCase(0UL, 1, TestName = "ZeroStillTakesOneProof")]
    [TestCase(3UL, 3, TestName = "AsRegistered")]
    [TestCase(17UL, int.MaxValue, TestName = "AboveWhatAQuorumCanReach")]
    public async Task Read_Threshold_IsWhatTheManagerAccepts(ulong stored, int expected)
    {
        ReturnsThreshold(stored);

        QuorumRegistration registration = await Reader().Read([Attester(FirstSystem, TestItem.AddressA)], CancellationToken.None);

        Assert.That(registration.Threshold, Is.EqualTo(expected), "a batch without proofs is never posted, and an unreachable threshold is never met");
    }

    [Test]
    public void Read_RollupWithoutAManagerYet_IsRetriedNextSlot()
    {
        Returns(Registry, "rollups(uint64)", new byte[96]);

        ProveException e = Assert.ThrowsAsync<ProveException>(() => Reader().Read([Attester(FirstSystem, TestItem.AddressA)], CancellationToken.None))!;

        Assert.That(e.Kind, Is.EqualTo(ProveFailureKind.Retryable), "the node may start before its rollup is registered");
    }

    [Test]
    public async Task Read_EverySlot_ReadsTheManagerOnce()
    {
        QuorumRegistrationReader reader = Reader();
        IAttester[] attesters = [Attester(FirstSystem, TestItem.AddressA)];

        await reader.Read(attesters, CancellationToken.None);
        await reader.Read(attesters, CancellationToken.None);

        await _l1.Received(1).Call(Registry, Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _l1.Received(2).Call(Manager, Arg.Is<byte[]>(static d => Calls(d, "threshold()", null)), Arg.Any<CancellationToken>());
    }

    private QuorumRegistrationReader Reader() => new(_l1, Registry, RollupId, LimboLogs.Instance);

    private void ReturnsThreshold(ulong threshold)
    {
        byte[] word = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(word.AsSpan(24), threshold);
        Returns(Manager, "threshold()", word);
    }

    private void Returns(Address to, string signature, byte[] result, Address? argument = null) =>
        _l1.Call(to, Arg.Is<byte[]>(d => Calls(d, signature, argument)), Arg.Any<CancellationToken>()).Returns(result);

    /// <summary>Whether <paramref name="data"/> calls <paramref name="signature"/>, with <paramref name="argument"/> as its address argument when given.</summary>
    private static bool Calls(byte[] data, string signature, Address? argument) =>
        data.AsSpan(0, 4).SequenceEqual(Selector(signature)) && (argument is null || data.AsSpan(4 + 12, 20).SequenceEqual(argument.Bytes));

    private static byte[] Selector(string signature) => Keccak.Compute(signature).Bytes[..4].ToArray();

    private static byte[] Word(Address address)
    {
        byte[] word = new byte[32];
        address.Bytes.CopyTo(word.AsSpan(12));
        return word;
    }

    private static IAttester Attester(Address proofSystem, Address signer)
    {
        IAttester attester = Substitute.For<IAttester>();
        attester.ProofSystem.Returns(proofSystem);
        attester.Signer.Returns(signer);
        return attester;
    }
}
