// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

/// <summary>EIP-8437 mode 2: raw signatures for recognized leaf programs.</summary>
public class RawWitnessTests
{
    private static readonly ValueHash256 ProgramKey = ValueKeccak.Compute("recognized program");
    private static readonly IReleaseSpec Batched = new OverridableReleaseSpec(Eip8288Prototype.Instance) { IsEipBatchedDependencyPricingEnabled = true };

    /// <summary>A recognized program whose native check accepts a signature exactly when its first byte is nonzero and whose claim is
    /// <c>keccak256(signature)</c>.</summary>
    private sealed class FakeRecognized : IRecognizedProgramVerifier
    {
        public int Verifications { get; private set; }
        public List<RawSignature> Proved { get; } = [];

        public bool Recognizes(in ValueHash256 verificationKey) => verificationKey == ProgramKey;

        public bool Verify(in ValueHash256 verificationKey, RawSignature signature, out ValueHash256 claim)
        {
            Verifications++;
            claim = ClaimOf(signature);
            return Recognizes(verificationKey) && signature.Signature[0] != 0;
        }

        public byte[] ProveLeaf(in ValueHash256 verificationKey, IReadOnlyList<RawSignature> signatures)
        {
            Proved.AddRange(signatures);
            return [7, 7];
        }
    }

    private static ValueHash256 ClaimOf(RawSignature signature) => ValueKeccak.Compute(signature.Signature);

    private static RawSignature Signature(byte seed) => new([seed, 2, 3], ValueKeccak.Compute([seed]), [seed, 5, 6]);

    private static FrameDependency RawDependency(RawSignature signature) => new(Eip8288Constants.LeanStarkScheme, ClaimOf(signature), ProgramKey);

    private static FrameDependency SphincsDependency(string seed) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(seed), default);

    private static Transaction Transaction(ulong nonce, params FrameDependency[] dependencies)
    {
        List<FrameDependency> declared = Eip8288Dependencies.Canonicalize(dependencies);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [],
            ChainId = 1,
            SenderAddress = TestItem.AddressA,
            Nonce = nonce,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, (ulong)declared.Sum(static d => (long)d.VerificationGas),
                UInt256.Zero, Eip8288Dependencies.Serialize(declared))]
        };
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    /// <summary>A mode-2 wrapper over <paramref name="transactions"/>: raw witnesses where <paramref name="raws"/> has one, else a
    /// one-byte ordinary witness.</summary>
    private static MempoolWrapper RawWrapper(Dictionary<FrameDependency, RawSignature> raws, params Transaction[] transactions)
    {
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(
            transactions.SelectMany(static t => Eip8288Dependencies.ForTransaction(t, FrameTxSigHash.ComputeValue)));
        return new MempoolWrapper
        {
            Transactions = [.. transactions.OrderBy(static t => t.Hash!.ToString())],
            Mode = MempoolWrapper.ModeRaw,
            Deps = deps,
            Proofs = [.. deps.Select(d => raws.ContainsKey(d) ? [] : new byte[] { 1 })],
            RawWitnesses = [.. deps.Select(d => raws.TryGetValue(d, out RawSignature? raw) ? raw : null)],
        };
    }

    private static byte[] Encode(MempoolWrapper wrapper) => MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes;

    private static MempoolWrapper Decode(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        return MempoolWrapperDecoder.Instance.Decode(ref reader)!;
    }

    private sealed record Node(ProofWrapperService Service, ITxPool Pool, LeanProofStore Store, RawWitnessPool Raw, FakeRecognized Recognized,
        FakeLeanProofVerifier Verifier, IChainHeadInfoProvider Head, ManualTimeProvider Clock, List<byte[]> Relayed);

    private static Node CreateNode(IReleaseSpec? spec = null, bool recognize = true, bool proofsValid = true)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        List<Transaction> pending = [];
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
        {
            pending.Add(call.Arg<Transaction>());
            return AcceptTxResult.Accepted;
        });
        pool.GetPendingTransactions().Returns(_ => [.. pending]);
        pool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction?>()).Returns(call =>
        {
            Transaction? found = pending.Find(t => t.Hash!.ValueHash256 == call.Arg<ValueHash256>());
            call[1] = found;
            return found is not null;
        });
        pool.GetPendingTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        pool.GetLatestPendingNonce(Arg.Any<Address>()).Returns(ulong.MaxValue);
        pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        IChainHeadInfoProvider head = Substitute.For<IChainHeadInfoProvider>();
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        ManualTimeProvider clock = new();
        TxPoolConfig config = new() { RecognizedPrograms = recognize ? ["slhdsa-shake128s-pure-v1"] : [] };
        RawWitnessPool raw = new(pool, head, config, clock: clock);
        FakeRecognized recognized = new();
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        FakeLeanProofVerifier verifier = new(proofsValid);
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(spec ?? Batched), finder, store, verifier,
            rawWitnesses: raw, recognizedPrograms: recognized);
        List<byte[]> relayed = [];
        service.WrapperValidated += (encoded, _) => relayed.Add(encoded);
        return new(service, pool, store, raw, recognized, verifier, head, clock, relayed);
    }

    private static void SetNonce(Node node, ulong nonce) => node.Head.ReadOnlyStateProvider.GetNonce(TestItem.AddressA).Returns(nonce);

    [Test]
    public void Mode_2_round_trips_raw_and_ordinary_entries()
    {
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        MempoolWrapper wrapper = RawWrapper(new() { [raw] = signature }, Transaction(0, raw, SphincsDependency("a")));

        byte[] encoded = Encode(wrapper);
        MempoolWrapper decoded = Decode(encoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Mode, Is.EqualTo(MempoolWrapper.ModeRaw));
            Assert.That(decoded.Deps, Is.EqualTo(wrapper.Deps));
            Assert.That(decoded.RawWitnesses!.Count(static r => r is not null), Is.EqualTo(1));
            Assert.That(decoded.RawWitnesses![decoded.Deps.ToList().IndexOf(raw)], Is.EqualTo(signature).Using<RawSignature>(SameRaw));
            Assert.That(Encode(decoded), Is.EqualTo(encoded));
        }
    }

    private static bool SameRaw(RawSignature a, RawSignature b) =>
        a.PublicKey.AsSpan().SequenceEqual(b.PublicKey) && a.Message == b.Message && a.Signature.AsSpan().SequenceEqual(b.Signature);

    // EIP-8437 Test Cases: a raw entry in mode 0, mode 2 with no raw entry, the wrong tuple shape or an entry over 16 KiB is
    // malformed; so is a raw entry for a non-generic dependency or an entry count that differs from the dependencies'.
    [TestCase("raw entry in mode 0")]
    [TestCase("no raw entry")]
    [TestCase("two fields")]
    [TestCase("four fields")]
    [TestCase("31-byte message")]
    [TestCase("entry over 16 KiB")]
    [TestCase("raw entry for leanSPHINCS")]
    [TestCase("missing entry")]
    public void Malformed_mode_2_framing_is_an_encoding_error(string scenario)
    {
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        FrameDependency sphincs = SphincsDependency("a");
        Transaction transaction = Transaction(0, raw, sphincs);
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize([raw, sphincs]);
        Rlp RawEntry(params byte[][] fields) => Rlp.Encode([.. fields.Select(static f => Rlp.Encode(f))]);
        Rlp good = RawEntry(signature.PublicKey, signature.Message.ToByteArray(), signature.Signature);
        Rlp ordinary = Rlp.Encode(new byte[] { 1 });
        Rlp Entry(FrameDependency d, Rlp rawEntry) => d.Equals(raw) ? rawEntry : ordinary;
        (byte Mode, Rlp[] Entries) framing = scenario switch
        {
            "raw entry in mode 0" => (MempoolWrapper.ModeDirect, deps.Select(d => Entry(d, good)).ToArray()),
            "no raw entry" => (MempoolWrapper.ModeRaw, deps.Select(_ => ordinary).ToArray()),
            "two fields" => (MempoolWrapper.ModeRaw, deps.Select(d => Entry(d, RawEntry(signature.PublicKey, signature.Message.ToByteArray()))).ToArray()),
            "four fields" => (MempoolWrapper.ModeRaw, deps.Select(d => Entry(d, RawEntry(signature.PublicKey, signature.Message.ToByteArray(), signature.Signature, [9]))).ToArray()),
            "31-byte message" => (MempoolWrapper.ModeRaw, deps.Select(d => Entry(d, RawEntry(signature.PublicKey, new byte[31], signature.Signature))).ToArray()),
            "entry over 16 KiB" => (MempoolWrapper.ModeRaw, deps.Select(d => Entry(d, RawEntry(signature.PublicKey, signature.Message.ToByteArray(), new byte[MempoolWrapper.MaxRawWitnessBytes]))).ToArray()),
            "raw entry for leanSPHINCS" => (MempoolWrapper.ModeRaw, deps.Select(d => d.Equals(sphincs) ? good : ordinary).ToArray()),
            "missing entry" => (MempoolWrapper.ModeRaw, new[] { good }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        byte[] encoded = Rlp.Encode(
            Rlp.Encode([Rlp.Encode(MempoolWrapperDecoder.EncodeTransaction(transaction))]),
            Rlp.Encode(framing.Mode),
            Rlp.Encode(Rlp.Encode([.. deps.Select(static d => { byte[] b = new byte[Eip8288Constants.DependencyTripleLength]; d.WriteTo(b); return Rlp.Encode(b); })]),
                Rlp.Encode(framing.Entries))).Bytes;

        Assert.That(() => Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Raw_witnesses_validate_beside_ordinary_witnesses()
    {
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        MempoolWrapper wrapper = RawWrapper(new() { [raw] = signature }, Transaction(0, raw, SphincsDependency("a")));
        FakeRecognized recognized = new();
        FakeLeanProofVerifier verifier = new(true);

        bool valid = MempoolWrapperValidator.Validate(wrapper, verifier, Eip8288Verifier.For(Batched), out string? error, recognized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.True, error);
            Assert.That(recognized.Verifications, Is.EqualTo(1));
            Assert.That(verifier.VerificationCalls, Is.EqualTo(1), "only the ordinary witness goes to the proof verifier");
        }
    }

    // EIP-8437: every failure is invalid, never malformed; an unrecognized key and the base guest decline without penalty.
    [TestCase("unrecognized key", MempoolWrapperValidator.UnrecognizedProgram)]
    [TestCase("no recognized programs", MempoolWrapperValidator.UnrecognizedProgram)]
    [TestCase("EIP-8288 guest", MempoolWrapperValidator.UnrecognizedProgram)]
    [TestCase("bad signature", MempoolWrapperValidator.InvalidRawWitness)]
    [TestCase("claim mismatch", MempoolWrapperValidator.InvalidRawWitness)]
    [TestCase("bad ordinary witness", MempoolWrapperValidator.InvalidProof)]
    public void Raw_witness_failures_are_invalid(string scenario, string expected)
    {
        RawSignature signature = scenario == "bad signature" ? new([1], ValueKeccak.Compute("m"), [0, 1]) : Signature(1);
        FrameDependency raw = scenario switch
        {
            "unrecognized key" => new(Eip8288Constants.LeanStarkScheme, ClaimOf(signature), ValueKeccak.Compute("other program")),
            "claim mismatch" => new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("another claim"), ProgramKey),
            _ => RawDependency(signature),
        };
        MempoolWrapper wrapper = RawWrapper(new() { [raw] = signature }, Transaction(0, raw, SphincsDependency("a")));
        IRecognizedProgramVerifier? recognized = scenario == "no recognized programs" ? null : new FakeRecognized();
        Eip8288Verifier forkVerifier = scenario == "EIP-8288 guest" ? Eip8288Verifier.Pinned : Eip8288Verifier.For(Batched);

        bool valid = MempoolWrapperValidator.Validate(wrapper, new FakeLeanProofVerifier(scenario != "bad ordinary witness"), forkVerifier,
            out string? error, recognized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Mode_0_dependency_limits_apply_to_mode_2()
    {
        Dictionary<FrameDependency, RawSignature> raws = [];
        for (byte i = 1; i <= Eip8288Constants.MaxLeanStarkDepsPerWrapper + 1; i++) raws[RawDependency(Signature(i))] = Signature(i);
        Transaction[] transactions = [.. raws.Keys.Select((d, i) => Transaction((ulong)i, d))];
        MempoolWrapper wrapper = RawWrapper(raws, transactions);

        bool valid = MempoolWrapperValidator.Validate(wrapper, new FakeLeanProofVerifier(true), Eip8288Verifier.For(Batched), out string? error, new FakeRecognized());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False);
            Assert.That(error, Is.EqualTo(MempoolWrapperValidator.TooManyStarkDeps));
        }
    }

    // EIP-8437: raw acceptance establishes no proof coverage, so a raw-held transaction is neither pending nor covered and creates
    // no inclusion-list obligation; the wrapper is relayed once.
    [Test]
    public async Task Raw_wrapper_is_held_and_relayed_without_entering_the_pool_or_the_proof_store()
    {
        Node node = CreateNode();
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        Transaction transaction = Transaction(0, raw);
        byte[] wrapper = Encode(RawWrapper(new() { [raw] = signature }, transaction));

        ProofWrapperAcceptance first = await node.Service.AcceptOwnedDetailedAsync(wrapper);
        ProofWrapperAcceptance repeated = await node.Service.AcceptOwnedDetailedAsync(wrapper);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), first.Result.Error);
            Assert.That(repeated.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), repeated.Result.Error);
            Assert.That(node.Relayed, Has.Count.EqualTo(1));
            Assert.That(node.Raw.Holds(transaction.Hash!.ValueHash256), Is.True);
            Assert.That(node.Store.Covers(transaction), Is.False);
            Assert.That(node.Pool.GetPendingTransactions(), Is.Empty);
            node.Pool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            Assert.That(node.Verifier.VerificationCalls, Is.Zero, "no proof verification for a raw-only wrapper");
        }
    }

    [Test]
    public async Task Raw_wrappers_are_declined_without_penalty_when_no_program_is_recognized()
    {
        Node node = CreateNode(recognize: false);
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);

        ProofWrapperAcceptance result = await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [raw] = signature }, Transaction(0, raw))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Service.AcceptsRawWitnesses, Is.False);
            Assert.That(result.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.LocalFailure));
            Assert.That(node.Recognized.Verifications, Is.Zero);
            Assert.That(node.Relayed, Is.Empty);
        }
    }

    // EIP-8437: raw candidates have their own verification budget, charged for every witness, raw or ordinary, and every
    // simulation, and kept per stable source identity across reconnections. Exhausting it refuses further raw work without
    // penalty and takes nothing from proof-backed admission.
    [Test]
    public async Task Spent_raw_budget_declines_raw_work_but_not_proof_backed_wrappers()
    {
        Node node = CreateNode();
        object nodeId = new();
        int accepted = 0, busy = 0;
        for (byte i = 1; i <= 10; i++)
        {
            RawSignature signature = Signature(i);
            FrameDependency raw = RawDependency(signature);
            // A fresh connection object each time: the budget follows the identity, not the connection.
            ProofWrapperAcceptance result = await node.Service.AcceptOwnedDetailedAsync(
                Encode(RawWrapper(new() { [raw] = signature }, Transaction(i, raw, SphincsDependency($"ordinary {i}")))), source: new object(), identity: nodeId);
            if (result.Status == ProofWrapperAcceptanceStatus.Busy) busy++;
            else if (result.Status == ProofWrapperAcceptanceStatus.Accepted) accepted++;
        }
        Transaction proven = Transaction(100, SphincsDependency("proven"));
        List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(proven, FrameTxSigHash.ComputeValue);
        ProofWrapperAcceptance proofBacked = await node.Service.AcceptOwnedDetailedAsync(Encode(new MempoolWrapper
        {
            Transactions = [proven], Mode = MempoolWrapper.ModeDirect, Deps = deps, Proofs = [[1]]
        }), source: nodeId);

        using (Assert.EnterMultipleScope())
        {
            // Two witnesses and one simulation per wrapper.
            Assert.That(accepted, Is.EqualTo((int)RawWitnessPool.MaxVerificationsPerSourcePerSecond / 3));
            Assert.That(busy, Is.EqualTo(10 - accepted));
            Assert.That(node.Recognized.Verifications, Is.EqualTo(accepted));
            Assert.That(proofBacked.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), proofBacked.Result.Error);
        }

        node.Clock.Advance(TimeSpan.FromSeconds(1));
        RawSignature later = Signature(200);
        FrameDependency laterDep = RawDependency(later);
        ProofWrapperAcceptance refilled = await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [laterDep] = later }, Transaction(200, laterDep))),
            source: new object(), identity: nodeId);
        Assert.That(refilled.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), refilled.Result.Error);
    }

    // Held and known-invalid wrappers are answered without validating, or charging, again.
    [Test]
    public async Task Repeated_wrappers_are_not_validated_again()
    {
        Node node = CreateNode();
        RawSignature signature = Signature(1), bad = new([1], ValueKeccak.Compute("m"), [0]);
        FrameDependency raw = RawDependency(signature);
        FrameDependency badDep = new(Eip8288Constants.LeanStarkScheme, ClaimOf(bad), ProgramKey);
        byte[] held = Encode(RawWrapper(new() { [raw] = signature }, Transaction(0, raw)));
        byte[] invalid = Encode(RawWrapper(new() { [badDep] = bad }, Transaction(1, badDep)));
        await node.Service.AcceptOwnedDetailedAsync(held);
        await node.Service.AcceptOwnedDetailedAsync(invalid);
        int verifications = node.Recognized.Verifications;

        ProofWrapperAcceptance heldAgain = await node.Service.AcceptOwnedDetailedAsync(held);
        ProofWrapperAcceptance invalidAgain = await node.Service.AcceptOwnedDetailedAsync(invalid);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldAgain.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted));
            Assert.That(invalidAgain.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Invalid));
            Assert.That(node.Recognized.Verifications, Is.EqualTo(verifications));
        }
    }

    [Test]
    public void Concurrent_raw_validations_are_capped()
    {
        Node node = CreateNode();
        for (int i = 0; i < RawWitnessPool.MaxConcurrentValidations; i++) Assert.That(node.Raw.TryEnterValidation(), Is.True);
        Assert.That(node.Raw.TryEnterValidation(), Is.False);
        node.Raw.ExitValidation();
        Assert.That(node.Raw.TryEnterValidation(), Is.True);
    }

    // When the global budget is full, the largest other source gives way, oldest first; a source never evicts itself.
    [Test]
    public void Full_raw_pool_evicts_the_largest_other_source()
    {
        Node node = CreateNode();
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        MempoolWrapper decoded = RawWrapper(new() { [raw] = signature }, Transaction(0, raw));
        object a = new(), b = new();
        static ValueHash256 Hash(int i) => ValueKeccak.Compute(BitConverter.GetBytes(i));
        for (int i = 0; i < RawWitnessPool.MaxWrappers; i++)
            Assert.That(node.Raw.TryAdd([1], Hash(i), decoded, Eip8288Verifier.For(Batched), a, a), Is.EqualTo(RawWitnessPool.Admission.Added));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Raw.TryAdd([1], Hash(-1), decoded, Eip8288Verifier.For(Batched), a, a), Is.EqualTo(RawWitnessPool.Admission.Full));
            Assert.That(node.Raw.TryAdd([1], Hash(-2), decoded, Eip8288Verifier.For(Batched), b, b), Is.EqualTo(RawWitnessPool.Admission.Added));
            Assert.That(node.Raw.Contains(Hash(0)), Is.False, "the oldest of the largest source gave way");
            Assert.That(node.Raw.Contains(Hash(1)), Is.True);
            Assert.That(node.Raw.Count, Is.EqualTo(RawWitnessPool.MaxWrappers));
        }
    }

    // EIP-8437: a covering proof admits the raw-held transaction as new, so its wrapper is relayed, and the raw candidate leaves.
    [Test]
    public async Task Covering_proof_admits_a_raw_held_transaction_as_new_and_releases_the_candidate()
    {
        Node node = CreateNode();
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        Transaction transaction = Transaction(0, raw);
        await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [raw] = signature }, transaction)));

        ProofWrapperAcceptance proven = await node.Service.AcceptOwnedDetailedAsync(Encode(new MempoolWrapper
        {
            Transactions = [transaction],
            Mode = MempoolWrapper.ModeDirect,
            Deps = [raw],
            Proofs = [[1]]
        }));
        node.Pool.NewPending += Raise.Event<EventHandler<TxEventArgs>>(node.Pool, new TxEventArgs(transaction));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proven.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), proven.Result.Error);
            Assert.That(node.Relayed, Has.Count.EqualTo(2));
            Assert.That(node.Store.Covers(transaction), Is.True);
            Assert.That(node.Raw.Holds(transaction.Hash!.ValueHash256), Is.False);
        }
    }

    // A raw wrapper adding nothing to a transaction that is already pending is accepted but not relayed.
    [Test]
    public async Task Raw_wrapper_for_an_already_pending_transaction_is_not_relayed()
    {
        Node node = CreateNode();
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        Transaction transaction = Transaction(0, raw);
        await node.Service.AcceptOwnedDetailedAsync(Encode(new MempoolWrapper
        {
            Transactions = [transaction],
            Mode = MempoolWrapper.ModeDirect,
            Deps = [raw],
            Proofs = [[1]]
        }));

        ProofWrapperAcceptance result = await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [raw] = signature }, transaction)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), result.Result.Error);
            Assert.That(node.Relayed, Has.Count.EqualTo(1));
        }
    }

    // EIP-8437: raw candidates are revalidated and evicted under ordinary pool rules, including expiry and nonce changes.
    [TestCase("stale nonce")]
    [TestCase("max age")]
    public async Task Raw_candidates_leave_on_staleness(string scenario)
    {
        Node node = CreateNode();
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        Transaction transaction = Transaction(0, raw);
        await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [raw] = signature }, transaction)));
        if (scenario == "stale nonce") SetNonce(node, 1);
        else node.Clock.Advance(RawWitnessPool.MaxAge + TimeSpan.FromSeconds(1));

        node.Pool.TxPoolHeadChanged += Raise.Event<EventHandler<Block>>(node.Pool, Build.A.Block.TestObject);

        Assert.That(node.Raw.Holds(transaction.Hash!.ValueHash256), Is.False);
    }

    [Test]
    public async Task Stale_transaction_is_not_held()
    {
        Node node = CreateNode();
        SetNonce(node, 5);
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);

        ProofWrapperAcceptance result = await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [raw] = signature }, Transaction(0, raw))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.PoolRejected));
            Assert.That(node.Raw.Count, Is.Zero);
            Assert.That(node.Relayed, Is.Empty);
        }
    }

    // A peer's raw candidates, once proven, are admitted as the peer's: never as local transactions.
    [Test]
    public async Task Prover_admits_proven_coverage_as_its_candidates_were([Values] bool local)
    {
        Node node = CreateNode();
        object peer = new();
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);
        byte[] wrapper = Encode(RawWrapper(new() { [raw] = signature }, Transaction(0, raw)));
        if (local) await node.Service.AcceptDetailedAsync(wrapper);
        else await node.Service.AcceptOwnedDetailedAsync(wrapper, source: peer, identity: peer);
        using RawWitnessProver prover = new(node.Service, node.Raw, node.Recognized, node.Verifier, new TxPoolConfig(), LimboLogs.Instance);

        ProofWrapperAcceptance? result = await prover.ProveOnceAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result?.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), result?.Result.Error);
            node.Pool.Received(1).SubmitTx(Arg.Any<Transaction>(), local ? TxHandlingOptions.PersistentBroadcast : TxHandlingOptions.None);
        }
    }

    [Test]
    public async Task Prover_folds_a_leaf_of_held_raw_signatures_into_an_admitted_mode_1_wrapper([Values(1, 2)] int maxClaims)
    {
        Node node = CreateNode();
        RawSignature first = Signature(1), second = Signature(2);
        FrameDependency firstDep = RawDependency(first), secondDep = RawDependency(second);
        FrameDependency ordinary = SphincsDependency("ordinary");
        Transaction a = Transaction(0, firstDep, ordinary), b = Transaction(1, secondDep);
        await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [firstDep] = first }, a)));
        await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [secondDep] = second }, b)));
        using RawWitnessProver prover = new(node.Service, node.Raw, node.Recognized, node.Verifier,
            new TxPoolConfig { RawWitnessMaxClaimsPerLeaf = maxClaims }, LimboLogs.Instance);

        ProofWrapperAcceptance? result = await prover.ProveOnceAsync();

        AggregationInput input = node.Verifier.LastInput!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result?.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), result?.Result.Error);
            Assert.That(node.Recognized.Proved, Has.Count.EqualTo(maxClaims));
            Assert.That(input.BatchLeaves, Has.Count.EqualTo(1));
            Assert.That(input.BatchLeaves[0].VerificationKeyHash, Is.EqualTo(ProgramKey));
            Assert.That(input.BatchLeaves[0].Claims, Is.EqualTo(maxClaims == 1 ? new[] { firstDep.DataHash } : new[] { firstDep.DataHash, secondDep.DataHash }.Order()));
            Assert.That(input.Deps, Is.EqualTo(new[] { ordinary }), "the ordinary witness is folded in directly");
            Assert.That(node.Pool.GetPendingTransactions(), Has.Length.EqualTo(maxClaims));
            Assert.That(node.Raw.Count, Is.EqualTo(2 - maxClaims));
            Assert.That(Decode(node.Relayed[^1]).Mode, Is.EqualTo(MempoolWrapper.ModeRecursive));
        }
    }

    // Recognized programs publish batch statements, which EIP-8288's guest does not take.
    [Test]
    public async Task Prover_and_admission_need_the_batch_pricing_guest()
    {
        Node node = CreateNode(spec: Eip8288Prototype.Instance);
        RawSignature signature = Signature(1);
        FrameDependency raw = RawDependency(signature);

        ProofWrapperAcceptance admission = await node.Service.AcceptOwnedDetailedAsync(Encode(RawWrapper(new() { [raw] = signature }, Transaction(0, raw))));
        using RawWitnessProver prover = new(node.Service, node.Raw, node.Recognized, node.Verifier, new TxPoolConfig(), LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(admission.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Invalid));
            Assert.That(admission.Result.Error, Is.EqualTo(MempoolWrapperValidator.UnrecognizedProgram));
            Assert.That(await prover.ProveOnceAsync(), Is.Null);
        }
    }

    [Test]
    public void Held_raw_wrapper_revalidates_under_the_same_verifier_only_when_its_raw_witnesses_pass()
    {
        Node node = CreateNode();
        RawSignature signature = Signature(1), bad = new([1], ValueKeccak.Compute("m"), [0]);
        FrameDependency raw = RawDependency(signature);
        FrameDependency badDep = new(Eip8288Constants.LeanStarkScheme, ClaimOf(bad), ProgramKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Service.Revalidates(Encode(RawWrapper(new() { [raw] = signature }, Transaction(0, raw))), Eip8288Verifier.For(Batched)), Is.True);
            Assert.That(node.Service.Revalidates(Encode(RawWrapper(new() { [raw] = signature }, Transaction(0, raw))), Eip8288Verifier.Pinned), Is.False);
            Assert.That(node.Service.Revalidates(Encode(RawWrapper(new() { [badDep] = bad }, Transaction(0, badDep))), Eip8288Verifier.For(Batched)), Is.False);
        }
    }
}
