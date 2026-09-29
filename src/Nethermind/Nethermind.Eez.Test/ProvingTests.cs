// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Proving;
using Nethermind.Eez.Sequencer;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class ProvingTests
{
    private const string Window = "captured-devnet-anchor-43722";
    private static readonly RollupTiming Timing = new(12_000, 2_000, 5_000, 100);

    /// <summary>For attesters that fail until the budget ends, so the retries end quickly.</summary>
    private static readonly RollupTiming ShortProofTime = new(12_000, 2_000, 300, 100);

    [Test]
    public async Task Attest_OneRemoteAttesterOnARecordedWindow_ReturnsItsValidProof()
    {
        await using AttesterServer server = await AttesterServer.Start(Window);
        JsonElement oracle = StatelessFixtures.ReadJson(Window, "oracle.json");
        Address proofSystem = new(oracle.GetProperty("proof_system").GetString()!);
        using RemoteAttester attester = new(server.Channel, proofSystem, AttesterServer.AttesterKey.Address);
        AttestationQuorum quorum = Quorum(attester);

        Attestation[] attestations = await quorum.Attest(RecordedRequest(), Registration(oracle), BundleTarget.NextBlock, CancellationToken.None);

        Assert.That(attestations.Select(static a => a.ProofSystem), Is.EqualTo(new[] { proofSystem }), "the one attester's proof system proves the window");
        Assert.That(quorum.Validity(attestations[0].Proof, new ValueHash256(oracle.GetProperty("public_inputs_hash").GetString()!), AttesterServer.AttesterKey.Address),
            Is.Null, "the proof is the attester's signature over the hash the reference signer signed for this window");
    }

    [Test]
    public async Task Attest_RegisteredWithAnotherKey_RejectsTheProof()
    {
        await using AttesterServer server = await AttesterServer.Start(Window);
        JsonElement oracle = StatelessFixtures.ReadJson(Window, "oracle.json");
        using RemoteAttester attester = new(server.Channel, new Address(oracle.GetProperty("proof_system").GetString()!), AttesterServer.AttesterKey.Address);

        ProveException e = Assert.ThrowsAsync<ProveException>(() => Quorum(attester).Attest(RecordedRequest(),
            new QuorumRegistration(1, [Keccak.Compute("another key").ValueHash256]), BundleTarget.NextBlock, CancellationToken.None))!;

        Assert.That(e.Kind, Is.EqualTo(ProveFailureKind.Backend), "a proof over another key's public inputs would revert the batch on L1, so it never counts");
    }

    [Test]
    public async Task Attest_TwoOfThreeValid_ReachesTheThresholdWithoutTheInvalidOne()
    {
        ProveRequest request = SyntheticRequest();
        PrivateKey rogue = TestItem.PrivateKeyC;
        IAttester[] attesters = [Signing(1, TestItem.PrivateKeyA), Signing(2, TestItem.PrivateKeyB), Signing(3, TestItem.PrivateKeyD, signWith: rogue)];

        Attestation[] attestations = await Quorum(attesters).Attest(request, Keys(3, threshold: 2), BundleTarget.NextBlock, CancellationToken.None);

        Assert.That(attestations.Select(static a => a.ProofSystem), Is.EqualTo(new[] { ProofSystem(1), ProofSystem(2) }),
            "the two valid proofs, ascending; the one signed with another key is left out");
    }

    [Test]
    public void Attest_BlockingSetRefusesOneEffect_ReportsItAsActionable()
    {
        RefusedEffect effect = new(true, 3, Keccak.Compute("outbound").ValueHash256);
        IAttester[] attesters = [Refusing(1, effect), Refusing(2, effect), Signing(3, TestItem.PrivateKeyA)];

        ProveException e = Assert.ThrowsAsync<ProveException>(() => Quorum(attesters).Attest(SyntheticRequest(), Keys(3, threshold: 2), BundleTarget.NextBlock,
            CancellationToken.None))!;

        Assert.That((e.Kind, e.Refused), Is.EqualTo((ProveFailureKind.Actionable, (RefusedEffect?)effect)),
            "two refusals of three with a threshold of two block the quorum, so the effect is dropped");
    }

    [Test]
    public void Attest_OneRefusalBelowTheBlockingSet_IsNotActionable()
    {
        RefusedEffect effect = new(true, 3, Keccak.Compute("outbound").ValueHash256);
        IAttester[] attesters = [Refusing(1, effect), Failing(2, ProveFailureKind.Retryable), Signing(3, TestItem.PrivateKeyA)];

        ProveException e = Assert.ThrowsAsync<ProveException>(() => Quorum(ShortProofTime, attesters).Attest(SyntheticRequest(), Keys(3, threshold: 2),
            BundleTarget.NextBlock, CancellationToken.None))!;

        Assert.That(e.Kind, Is.EqualTo(ProveFailureKind.Backend), "one attester alone cannot evict an effect the others did not refuse");
    }

    [Test]
    public void Attest_ThresholdAboveTheRegisteredAttesters_IsRefusedBeforeAsking()
    {
        IAttester attester = Signing(1, TestItem.PrivateKeyA);

        Assert.ThrowsAsync<ProveException>(() => Quorum(attester).Attest(SyntheticRequest(), Keys(1, threshold: 2), BundleTarget.NextBlock, CancellationToken.None),
            "a threshold the registered attesters cannot meet fails fast");
        attester.DidNotReceiveWithAnyArgs().Prove(default!, default);
    }

    [Test]
    public void Budget_PinnedBatchPastItsCutoff_IsRetryable()
    {
        ProveRetry retry = new(Timing, Substitute.For<ITimestamper>());
        DateTimeOffset slot = DateTimeOffset.FromUnixTimeSeconds(1_000);

        Assert.That(retry.Budget(new BundleTarget(10, 1_000), slot - TimeSpan.FromMilliseconds(5_200)), Is.EqualTo(TimeSpan.FromMilliseconds(100)),
            "the proof must start proof time plus slack before the pinned L1 block");
        Assert.That(() => retry.Budget(new BundleTarget(10, 1_000), slot - TimeSpan.FromMilliseconds(5_000)),
            Throws.TypeOf<ProveException>().With.Property(nameof(ProveException.Kind)).EqualTo(ProveFailureKind.Retryable),
            "a proof that can no longer land in its slot is not attempted");
    }

    [TestCase(1, 80, 120, TestName = "FirstRetry")]
    [TestCase(2, 160, 240, TestName = "SecondRetry")]
    [TestCase(5, 800, 1_200, TestName = "Capped")]
    [TestCase(40, 800, 1_200, TestName = "StaysCapped")]
    public void Backoff_FailedAttempt_DoublesWithJitterUpToOneSecond(int attempt, int lowMs, int highMs)
    {
        foreach (int seed in (int[])[0, 1, 7, int.MaxValue, -1])
        {
            Assert.That(ProveRetry.Backoff(attempt, seed).TotalMilliseconds, Is.InRange(lowMs, highMs), $"backoff after attempt {attempt} with seed {seed}");
        }
    }

    [Test]
    public void WitnessStore_PutThenFind_ReadsBackEveryList()
    {
        WitnessStore store = new(new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).TestObject;
        using Witness stored = Witness([0x01, 0x02], [0x03], [0x04], [0x05, 0x06]);
        store.Put(header, stored);

        using Witness found = store.Find(5, header.Hash!)!;

        Assert.That((found.State.ToArray(), found.Codes.ToArray(), found.Keys.ToArray(), found.Headers.ToArray()),
            Is.EqualTo((new byte[][] { [0x01, 0x02] }, new byte[][] { [0x03] }, new byte[][] { [0x04] }, new byte[][] { [0x05, 0x06] })),
            "state, codes, keys and headers read back each as its own list");
    }

    [Test]
    public void WitnessStore_PruneBelow_DropsEveryLowerBlockAndKeepsTheRest()
    {
        WitnessStore store = new(new MemDb());
        BlockHeader[] headers = [.. Enumerable.Range(4, 3).Select(static n => Build.A.BlockHeader.WithNumber(n).TestObject)];
        foreach (BlockHeader header in headers)
        {
            using Witness witness = Witness([0x01], [0x02], [], []);
            store.Put(header, witness);
        }

        store.PruneBelow(6);
        store.PruneBelow(5);

        Assert.That(headers.Select(h => store.Find((ulong)h.Number, h.Hash!) is { } w && Dispose(w)), Is.EqualTo(new[] { false, false, true }),
            "blocks below the pruning height are gone, a lower height later prunes nothing back, and the height itself is kept");
    }

    [Test]
    public void ForgetThrough_Finalized_PrunesEverythingUpToIt()
    {
        IWitnessStore store = Substitute.For<IWitnessStore>();

        new WitnessRecorder(Substitute.For<IWitnessGeneratingBlockProcessingEnvFactory>(), store).ForgetThrough(9);

        store.Received(1).PruneBelow(10);
    }

    [TestCase(Mutation.None, null, TestName = "Valid")]
    [TestCase(Mutation.Truncated, "bytes", TestName = "NotSixtyFiveBytes")]
    [TestCase(Mutation.RecoveryByte, "recovery byte", TestName = "RecoveryByteNot27Or28")]
    [TestCase(Mutation.HighS, "high s", TestName = "HighS")]
    [TestCase(Mutation.OtherDigest, "recovers to", TestName = "SignedOverAnotherHash")]
    public void Validity_Proof_IsRefusedWhenL1WouldRejectIt(Mutation mutation, string? reason)
    {
        ValueHash256 digest = Keccak.Compute("public inputs").ValueHash256;
        byte[] proof = new EezAttestationSigner(TestItem.PrivateKeyA).Sign(mutation == Mutation.OtherDigest ? Keccak.Compute("other").ValueHash256 : digest);
        proof = mutation switch
        {
            Mutation.Truncated => proof[..64],
            Mutation.RecoveryByte => [.. proof[..64], 29],
            Mutation.HighS => Malleated(proof),
            _ => proof,
        };

        string? invalid = Quorum().Validity(proof, digest, TestItem.PrivateKeyA.Address);

        Assert.That(invalid, reason is null ? Is.Null : Does.Contain(reason), "only a proof the EEZ contract accepts counts toward the quorum");
    }

    [TestCase(true, 800, 250, 250, TestName = "PinnedWithTimeToSpare")]
    [TestCase(true, 250, 250, 150, TestName = "PinnedCutToTheSubmission")]
    [TestCase(true, 50, 250, 0, TestName = "PinnedPastTheSubmission")]
    [TestCase(false, 50, 250, 250, TestName = "Unpinned")]
    public void Grace_PinnedBatch_EndsBeforeItMustBeSubmitted(bool pinned, int msBeforeTheSlot, int graceMs, int expectedMs)
    {
        DateTimeOffset slot = DateTimeOffset.FromUnixTimeSeconds(1_000);
        ManualTimestamper clock = new((slot - TimeSpan.FromMilliseconds(msBeforeTheSlot)).UtcDateTime);
        BundleTarget target = pinned ? new BundleTarget(10, 1_000) : BundleTarget.NextBlock;

        TimeSpan grace = new ProveRetry(Timing, clock).Grace(target, TimeSpan.FromMilliseconds(graceMs));

        Assert.That(grace, Is.EqualTo(TimeSpan.FromMilliseconds(expectedMs)), "collecting more proofs never costs a pinned batch its block");
    }

    public enum Mutation
    {
        None,
        Truncated,
        RecoveryByte,
        HighS,
        OtherDigest,
    }

    /// <summary>The same signature with s replaced by n - s and the recovery byte flipped: it recovers to the same signer.</summary>
    private static byte[] Malleated(byte[] proof)
    {
        UInt256 order = new(Convert.FromHexString("fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141"), isBigEndian: true);
        UInt256 s = new(proof.AsSpan(32, 32), isBigEndian: true);
        byte[] malleated = [.. proof];
        (order - s).ToBigEndian().CopyTo(malleated.AsSpan(32, 32));
        malleated[64] = (byte)(proof[64] == 27 ? 28 : 27);
        return malleated;
    }

    private static bool Dispose(Witness witness)
    {
        witness.Dispose();
        return true;
    }

    private static AttestationQuorum Quorum(params IAttester[] attesters) => Quorum(Timing, attesters);

    private static AttestationQuorum Quorum(RollupTiming timing, params IAttester[] attesters) =>
        new(attesters, new ProveRetry(timing, Timestamper.Default), TimeSpan.Zero, LimboLogs.Instance);

    private static ProveRequest RecordedRequest()
    {
        JsonElement oracle = StatelessFixtures.ReadJson(Window, "oracle.json");
        PostBatch mined = EezCalldata.DecodePostAndVerifyBatch(StatelessFixtures.ReadPostBatch(Window));
        ProvedBlock[] blocks = StatelessFixtures.ReadJson(Window, "blocks.json").EnumerateArray().Select(static e =>
        {
            ulong number = e.GetProperty("number").GetUInt64();
            return new ProvedBlock(number, new Hash256(e.GetProperty("hash").GetString()!), new Hash256(e.GetProperty("parent_hash").GetString()!),
                StatelessFixtures.ReadBlock(Window, $"block-{number}.rlp.hex"), StatelessFixtures.ReadWitness(Window, $"witness-{number}.json"));
        }).ToArray();
        return new ProveRequest(oracle.GetProperty("rollup_id").GetUInt64(), blocks[0].Number, blocks[^1].Number, mined with { Proofs = [] }, blocks);
    }

    private static QuorumRegistration Registration(JsonElement oracle) => new(1, [new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!)]);

    private static ProveRequest SyntheticRequest() =>
        new(1, 1, 1, AnchorBatch.Build(1, Keccak.Compute("settled").ValueHash256, Keccak.Compute("last").ValueHash256, [new DaBlock(Address.Zero, [], [])], []), []);

    private static QuorumRegistration Keys(int attesters, int threshold) =>
        new(threshold, [.. Enumerable.Range(1, attesters).Select(static i => Keccak.Compute($"key {i}").ValueHash256)]);

    private static Address ProofSystem(int index) => new($"0x{index:x40}");

    /// <summary>An attester that signs the public inputs hash its own batch has under key <c>key {index}</c>.</summary>
    private static IAttester Signing(int index, PrivateKey key, PrivateKey? signWith = null)
    {
        IAttester attester = Attester(index, key.Address);
        attester.Prove(Arg.Any<ProveRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            ProveRequest own = call.ArgAt<ProveRequest>(0);
            ValueHash256 digest = PostBatchProfile.PublicInputsHash(own.Batch, own.RollupId, Keccak.Compute($"key {index}").ValueHash256);
            return Task.FromResult(new EezAttestationSigner(signWith ?? key).Sign(digest));
        });
        return attester;
    }

    private static IAttester Refusing(int index, RefusedEffect effect) => Failing(index, ProveFailureKind.Actionable, effect);

    private static IAttester Failing(int index, ProveFailureKind kind, RefusedEffect? effect = null)
    {
        IAttester attester = Attester(index, TestItem.AddressA);
        attester.Prove(Arg.Any<ProveRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<byte[]>(new ProveException(kind, "refused", effect)));
        return attester;
    }

    private static IAttester Attester(int index, Address signer)
    {
        IAttester attester = Substitute.For<IAttester>();
        attester.ProofSystem.Returns(ProofSystem(index));
        attester.Signer.Returns(signer);
        return attester;
    }

    private static Witness Witness(byte[] state, byte[] code, byte[] key, byte[] header) => new()
    {
        State = new ArrayPoolList<byte[]>(1) { state },
        Codes = new ArrayPoolList<byte[]>(1) { code },
        Keys = key.Length == 0 ? new ArrayPoolList<byte[]>(0) : new ArrayPoolList<byte[]>(1) { key },
        Headers = header.Length == 0 ? new ArrayPoolList<byte[]>(0) : new ArrayPoolList<byte[]>(1) { header },
    };
}
