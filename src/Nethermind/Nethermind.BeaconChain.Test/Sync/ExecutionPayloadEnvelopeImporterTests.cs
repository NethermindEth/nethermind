// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// <see cref="ExecutionPayloadEnvelopeImporter"/> driven through the production <see cref="EngineDriver"/>
/// over a scripted engine RPC module, so the engine's own status strings are what gets mapped.
/// </summary>
public class ExecutionPayloadEnvelopeImporterTests
{
    private static readonly StringLabel EnvelopeRejected = new("execution_payload_envelope");

    /// <summary>
    /// SYNCING and ACCEPTED are "not rejected, not validated". Reporting either as
    /// <see cref="ExecutionPayloadEnvelopeImportResult.Valid"/> would let the caller mark the block's
    /// payload valid in fork choice, which cannot be unwound.
    /// </summary>
    [TestCase(PayloadStatus.Valid, ExecutionPayloadEnvelopeImportResult.Valid, 0)]
    [TestCase(PayloadStatus.Syncing, ExecutionPayloadEnvelopeImportResult.Optimistic, 0)]
    [TestCase(PayloadStatus.Accepted, ExecutionPayloadEnvelopeImportResult.Optimistic, 0)]
    [TestCase(PayloadStatus.Invalid, ExecutionPayloadEnvelopeImportResult.Invalid, 1)]
    public void Maps_each_engine_status_onto_its_own_verdict(string engineStatus, ExecutionPayloadEnvelopeImportResult expected, long expectedRejections)
    {
        BeaconStateGloas state = StateWithCommittedBid(out SignedExecutionPayloadBid bid, out Bls.SecretKey builderSk);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = engineStatus }));
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates().Add(state), engine).Import(envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(Rejections() - rejectionsBefore, Is.EqualTo(expectedRejections));
            engine.ReceivedWithAnyArgs(1).engine_newPayloadV5(default!, default!, default, default);
        }
    }

    [Test]
    public void An_envelope_for_an_unknown_block_is_not_rejected_and_never_reaches_availability_or_the_engine()
    {
        BeaconStateGloas state = StateWithCommittedBid(out SignedExecutionPayloadBid bid, out Bls.SecretKey builderSk);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        bool availabilityAsked = false;
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates(), engine, (_, _) => availabilityAsked = true).Import(envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock));
            Assert.That(availabilityAsked, Is.False);
            Assert.That(Rejections(), Is.EqualTo(rejectionsBefore), "an envelope may arrive before its block; that is not an invalid message");
            engine.DidNotReceiveWithAnyArgs().engine_newPayloadV5(default!, default!, default, default);
        }
    }

    [Test]
    public void An_envelope_whose_blob_data_is_unavailable_is_deferred_without_an_engine_call()
    {
        BeaconStateGloas state = StateWithCommittedBid(out SignedExecutionPayloadBid bid, out Bls.SecretKey builderSk);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates().Add(state), engine, (_, _) => false).Import(envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.DataUnavailable));
            Assert.That(Rejections(), Is.EqualTo(rejectionsBefore));
            engine.DidNotReceiveWithAnyArgs().engine_newPayloadV5(default!, default!, default, default);
        }
    }

    /// <summary>
    /// Each envelope is validly signed by the registered builder key, so only the spec checks can
    /// refuse them: one commits to a block hash the block's bid never named, the others name a
    /// builder index outside the one-entry registry (the first index past its end, and one far past it),
    /// which must be a rejection rather than an index exception.
    /// </summary>
    [TestCase(0ul, (byte)0x77, TestName = "Rejects_an_envelope_that_does_not_match_the_committed_bid")]
    [TestCase(1ul, (byte)0x88, TestName = "Rejects_an_envelope_naming_the_first_builder_index_past_the_registry")]
    [TestCase(7ul, (byte)0x88, TestName = "Rejects_an_envelope_naming_a_builder_outside_the_registry")]
    public void A_failed_verification_is_a_counted_rejection_decided_before_the_engine_is_called(ulong builderIndex, byte envelopeBlockHashFill)
    {
        BeaconStateGloas state = StateWithCommittedBid(out _, out Bls.SecretKey builderSk);
        Assert.That(state.Builders!, Has.Length.EqualTo(1), "fixture bug: the builder indices above assume a one-entry registry");
        ExecutionPayloadBid envelopeBid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei, blockHashFill: envelopeBlockHashFill).Message!;
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, envelopeBid, builderSk, builderIndex);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates().Add(state), engine).Import(envelope);

        AssertCountedRejectionWithoutEngineCall(result, rejectionsBefore, engine);
    }

    /// <summary>
    /// A self-built envelope names <c>BUILDER_INDEX_SELF_BUILD</c>, which is never inside the builder
    /// registry; it is signed by the block's proposer and must still verify.
    /// </summary>
    [Test]
    public void A_self_built_envelope_signed_by_the_proposer_is_accepted()
    {
        BeaconStateGloas state = StateWithSelfBuildBid(out SignedExecutionPayloadBid bid, out PubkeyCache pubkeys, out _);
        Bls.SecretKey proposerSk = ValidatorKey((int)state.LatestBlockHeader!.ProposerIndex);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, proposerSk, Presets.BuilderIndexSelfBuild);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates().Add(state), engine, pubkeys: pubkeys).Import(envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
            Assert.That(Rejections(), Is.EqualTo(rejectionsBefore));
            engine.ReceivedWithAnyArgs(1).engine_newPayloadV5(default!, default!, default, default);
        }
    }

    /// <summary>
    /// The envelope is otherwise valid; only its signature is replaced. A wrong key or a signature
    /// made under a domain other than <c>DOMAIN_BEACON_BUILDER</c> must fail
    /// <c>verify_execution_payload_envelope_signature</c> on both paths: a registered builder's
    /// envelope, and a self-built one, which the block's proposer signs.
    /// </summary>
    [TestCase(false, false, TestName = "Rejects_a_builder_envelope_signed_by_a_key_other_than_the_builder")]
    [TestCase(false, true, TestName = "Rejects_a_builder_envelope_signed_by_the_builder_under_the_proposer_domain")]
    [TestCase(true, false, TestName = "Rejects_a_self_built_envelope_signed_by_a_validator_other_than_the_proposer")]
    [TestCase(true, true, TestName = "Rejects_a_self_built_envelope_signed_by_the_proposer_under_the_proposer_domain")]
    public void A_wrongly_signed_envelope_is_a_counted_rejection_decided_before_the_engine_is_called(bool selfBuild, bool rightKeyWrongDomain)
    {
        BeaconStateGloas state;
        SignedExecutionPayloadEnvelope envelope;
        Bls.SecretKey rightKey;
        Bls.SecretKey otherKey;
        PubkeyCache pubkeys;
        if (selfBuild)
        {
            state = StateWithSelfBuildBid(out SignedExecutionPayloadBid bid, out pubkeys, out _);
            int proposerIndex = (int)state.LatestBlockHeader!.ProposerIndex;
            rightKey = ValidatorKey(proposerIndex);
            otherKey = ValidatorKey((proposerIndex + 1) % state.Validators!.Length);
            envelope = ValidEnvelope(state, bid.Message!, rightKey, Presets.BuilderIndexSelfBuild);
        }
        else
        {
            state = StateWithCommittedBid(out SignedExecutionPayloadBid bid, out rightKey);
            pubkeys = new PubkeyCache();
            otherKey = DeriveKey(201);
            envelope = ValidEnvelope(state, bid.Message!, rightKey, builderIndex: 0);
        }
        Assert.That(Resigned(state, envelope, rightKey, DomainType.BeaconBuilder).Signature, Is.EqualTo(envelope.Signature), "fixture bug: re-signing must reproduce the valid signature when nothing is changed");
        SignedExecutionPayloadEnvelope forged = rightKeyWrongDomain
            ? Resigned(state, envelope, rightKey, DomainType.BeaconProposer)
            : Resigned(state, envelope, otherKey, DomainType.BeaconBuilder);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates().Add(state), engine, pubkeys: pubkeys).Import(forged);

        AssertCountedRejectionWithoutEngineCall(result, rejectionsBefore, engine);
    }

    /// <summary>
    /// One field of an otherwise valid, correctly re-signed envelope is changed so that exactly one
    /// assert of <c>verify_execution_payload_envelope</c> (specs/gloas/fork-choice.md) fails. The store
    /// answers the envelope's own root, so the lookup never decides the outcome; only that assert does.
    /// </summary>
    [TestCaseSource(nameof(SpecCheckViolations))]
    public void An_envelope_failing_a_spec_check_is_a_counted_rejection_decided_before_the_engine_is_called(Action<ExecutionPayloadEnvelope> violate)
    {
        BeaconStateGloas state = StateWithCommittedBid(out SignedExecutionPayloadBid bid, out Bls.SecretKey builderSk);
        state.PayloadExpectedWithdrawals = [ExpectedWithdrawal()];
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        envelope.Message!.Payload!.Withdrawals = [ExpectedWithdrawal()];
        Assert.That(CreateImporter(new BlockStates().Add(state), ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }))).Import(envelope),
            Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "fixture bug: the envelope must verify before its one field is changed");

        violate(envelope.Message);
        SignedExecutionPayloadEnvelope forged = Resigned(state, envelope, builderSk, DomainType.BeaconBuilder);
        IGloasBlockStateProvider states = Substitute.For<IGloasBlockStateProvider>();
        states.GetGloasBlockState(forged.Message!.BeaconBlockRoot!).Returns(state);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(states, engine).Import(forged);

        AssertCountedRejectionWithoutEngineCall(result, rejectionsBefore, engine);
    }

    private static IEnumerable<TestCaseData> SpecCheckViolations()
    {
        yield return Violation("Rejects_an_envelope_whose_beacon_block_root_is_not_the_root_of_the_states_block", e => e.BeaconBlockRoot = Hash(0x5A));
        yield return Violation("Rejects_an_envelope_whose_parent_beacon_block_root_is_not_the_blocks_parent", e => e.ParentBeaconBlockRoot = Hash(0x5B));
        yield return Violation("Rejects_an_envelope_whose_prev_randao_differs_from_the_committed_bid", e => e.Payload!.PrevRandao = Hash(0x5C));
        yield return Violation("Rejects_an_envelope_whose_gas_limit_differs_from_the_committed_bid", e => e.Payload!.GasLimit += 1);
        yield return Violation("Rejects_an_envelope_whose_execution_requests_differ_from_the_committed_bid", e => e.ExecutionRequests = new ExecutionRequestsGloas
        {
            Withdrawals = [new WithdrawalRequest { SourceAddress = Address.Zero, ValidatorPubkey = Pubkey(0x51), Amount = 1 }],
        });
        yield return Violation("Rejects_an_envelope_whose_slot_number_is_not_the_state_slot", e => e.Payload!.SlotNumber += 1);
        yield return Violation("Rejects_an_envelope_whose_parent_hash_is_not_the_latest_block_hash", e => e.Payload!.ParentHash = Hash(0x5D));
        yield return Violation("Rejects_an_envelope_whose_timestamp_is_not_the_slot_time", e => e.Payload!.Timestamp += 1);
        yield return Violation("Rejects_an_envelope_missing_an_expected_withdrawal", e => e.Payload!.Withdrawals = []);
        yield return Violation("Rejects_an_envelope_whose_withdrawal_index_differs_from_the_expected", e => e.Payload!.Withdrawals![0].Index += 1);
        yield return Violation("Rejects_an_envelope_whose_withdrawal_validator_differs_from_the_expected", e => e.Payload!.Withdrawals![0].ValidatorIndex += 1);
        yield return Violation("Rejects_an_envelope_whose_withdrawal_address_differs_from_the_expected", e => e.Payload!.Withdrawals![0].Address = Address.Zero);
        yield return Violation("Rejects_an_envelope_whose_withdrawal_amount_differs_from_the_expected", e => e.Payload!.Withdrawals![0].Amount += 1);

        static TestCaseData Violation(string name, Action<ExecutionPayloadEnvelope> violate) => new TestCaseData(violate).SetName(name);
    }

    /// <summary>
    /// The block committed a self-build bid, but a registered builder delivers the envelope, validly
    /// signed with its own key and matching every other field. Only
    /// <c>envelope.builder_index == bid.builder_index</c> stops a builder from filling a slot it never won.
    /// </summary>
    [Test]
    public void Rejects_an_envelope_from_a_builder_other_than_the_one_the_block_committed_to()
    {
        BeaconStateGloas state = StateWithSelfBuildBid(out SignedExecutionPayloadBid bid, out PubkeyCache pubkeys, out Bls.SecretKey builderSk);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates().Add(state), engine, pubkeys: pubkeys).Import(envelope);

        AssertCountedRejectionWithoutEngineCall(result, rejectionsBefore, engine);
    }

    [Test]
    public void An_envelope_without_a_beacon_block_root_is_a_counted_rejection_that_never_reaches_availability_or_the_engine()
    {
        BeaconStateGloas state = StateWithCommittedBid(out SignedExecutionPayloadBid bid, out Bls.SecretKey builderSk);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        envelope.Message!.BeaconBlockRoot = null;
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid }));
        bool availabilityAsked = false;
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates().Add(state), engine, (_, _) => availabilityAsked = true).Import(envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Invalid), "a missing root can never become known, so retrying it as an unknown block would never end");
            Assert.That(Rejections() - rejectionsBefore, Is.EqualTo(1));
            Assert.That(availabilityAsked, Is.False);
            engine.DidNotReceiveWithAnyArgs().engine_newPayloadV5(default!, default!, default, default);
        }
    }

    [Test]
    public void An_engine_that_returns_no_verdict_defers_the_envelope_and_is_not_counted_as_a_rejection()
    {
        BeaconStateGloas state = StateWithCommittedBid(out SignedExecutionPayloadBid bid, out Bls.SecretKey builderSk);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        IEngineRpcModule engine = ScriptedEngine(ResultWrapper<PayloadStatusV1>.Fail("engine unavailable"));
        long rejectionsBefore = Rejections();

        ExecutionPayloadEnvelopeImportResult result = CreateImporter(new BlockStates().Add(state), engine).Import(envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.EngineUnavailable));
            Assert.That(Rejections(), Is.EqualTo(rejectionsBefore), "an unevaluated envelope is not an invalid one");
        }
    }

    /// <summary>
    /// Two retained blocks whose post-states committed different bids. Each envelope must be judged
    /// against its own block: availability is asked about that block's bid, the engine is sent that
    /// envelope's payload, and each verdict is the one the engine gave for that payload.
    /// </summary>
    [Test]
    public void Each_envelope_is_verified_against_the_post_state_of_its_own_block()
    {
        BeaconStateGloas stateA = StateWithCommittedBid(out SignedExecutionPayloadBid bidA, out Bls.SecretKey builderSk, blockHashFill: 0x88);
        BeaconStateGloas stateB = StateWithCommittedBid(out SignedExecutionPayloadBid bidB, out _, blockHashFill: 0x89);
        Hash256 rootA = BlockRootOf(stateA);
        Hash256 rootB = BlockRootOf(stateB);
        Assert.That(rootB, Is.Not.EqualTo(rootA), "fixture bug: the two states must be the post-states of different blocks");

        PostStateCache states = new(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), SyntheticSpec(), Hash(0x01), new BeaconStateFulu());
        states.RetainGloas(rootA, stateA);
        states.RetainGloas(rootB, stateB);

        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV5(Arg.Is<ExecutionPayloadV4>(p => p.BlockHash == Hash(0x88)), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
            .Returns(Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid })));
        engine.engine_newPayloadV5(Arg.Is<ExecutionPayloadV4>(p => p.BlockHash == Hash(0x89)), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
            .Returns(Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Syncing })));
        List<(Hash256 Root, Hash256 CommittedBlockHash)> availabilityAsked = [];
        ExecutionPayloadEnvelopeImporter importer = CreateImporter(states, engine, (root, committed) =>
        {
            availabilityAsked.Add((root, committed.BlockHash!));
            return true;
        });

        // B first: an importer that judged by the most recently retained or imported block would pass A's bid here.
        ExecutionPayloadEnvelopeImportResult resultB = importer.Import(ValidEnvelope(stateB, bidB.Message!, builderSk, builderIndex: 0));
        ExecutionPayloadEnvelopeImportResult resultA = importer.Import(ValidEnvelope(stateA, bidA.Message!, builderSk, builderIndex: 0));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resultB, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Optimistic));
            Assert.That(resultA, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
            Assert.That(availabilityAsked, Is.EqualTo(new[] { (rootB, Hash(0x89)), (rootA, Hash(0x88)) }));
        }
    }

    private static BeaconStateGloas StateWithCommittedBid(out SignedExecutionPayloadBid bid, out Bls.SecretKey builderSk, byte blockHashFill = 0x88)
    {
        BeaconStateGloas state = CreateGloasState(out builderSk, out _);
        bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei, blockHashFill);
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);
        return state;
    }

    private static BeaconStateGloas StateWithSelfBuildBid(out SignedExecutionPayloadBid bid, out PubkeyCache pubkeys, out Bls.SecretKey builderSk)
    {
        BeaconStateGloas state = CreateGloasState(out builderSk, out _);
        pubkeys = InstallRealValidatorKeys(state);
        bid = SelfBuildBid(state, state.LatestBlockHash!, Hash(0x88));
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), pubkeys, verifySignature: true);
        return state;
    }

    private static SignedExecutionPayloadEnvelope Resigned(BeaconStateGloas state, SignedExecutionPayloadEnvelope envelope, Bls.SecretKey signer, ReadOnlySpan<byte> domainType)
    {
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(envelope.Message!), state.GetDomain(domainType));
        return new SignedExecutionPayloadEnvelope { Message = envelope.Message, Signature = new BlsSignature(BlsSigner.Sign(signer, signingRoot.Bytes).Bytes) };
    }

    private static Withdrawal ExpectedWithdrawal() => new() { Index = 5, ValidatorIndex = 1, Address = new Address(BuilderWithdrawalCredentials(0xC1).Bytes[12..]), Amount = 7 * Gwei };

    private static ExecutionPayloadEnvelopeImporter CreateImporter(IGloasBlockStateProvider states, IEngineRpcModule engine, Func<Hash256, ExecutionPayloadBid, bool>? isDataAvailable = null, PubkeyCache? pubkeys = null)
    {
        ExternalClDetector detector = new(new BeaconChainConfig { Enabled = true }, new Lazy<IEngineRpcModule>(engine), LimboLogs.Instance);
        _ = new ExternalClInterceptingEngineRpcModule(engine, detector);
        EngineDriver driver = new(detector, LimboLogs.Instance);
        return new ExecutionPayloadEnvelopeImporter(states, driver, pubkeys ?? new PubkeyCache(), isDataAvailable ?? ((_, _) => true), LimboLogs.Instance);
    }

    private static IEngineRpcModule ScriptedEngine(ResultWrapper<PayloadStatusV1> answer)
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV5(default!, default!, default, default).ReturnsForAnyArgs(Task.FromResult(answer));
        return engine;
    }

    private static void AssertCountedRejectionWithoutEngineCall(ExecutionPayloadEnvelopeImportResult result, long rejectionsBefore, IEngineRpcModule engine)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Invalid));
            Assert.That(Rejections() - rejectionsBefore, Is.EqualTo(1));
            engine.DidNotReceiveWithAnyArgs().engine_newPayloadV5(default!, default!, default, default);
        }
    }

    private static long Rejections() => Metrics.BeaconChainForkChoiceRejections.GetValueOrDefault(EnvelopeRejected);
}
