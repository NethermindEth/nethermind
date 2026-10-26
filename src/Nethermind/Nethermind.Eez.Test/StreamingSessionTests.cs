// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class StreamingSessionTests
{
    private static readonly ByteString AnchorHash = Hash(1);
    private static readonly ByteString AnchorRoot = Hash(2);

    [Test]
    public void Begin_ReportsOnlyAnchor_AndCopiesMutableInputs()
    {
        Begin begin = Anchor();
        StreamingSession session = new("composer", begin, Limits());
        begin.AnchorHash = Hash(9);
        ServerFrame ready = session.Ready(session.Binding, 73);
        Assert.That((ready.SessionId.Length, ready.RequestId, ready.Epoch, ready.Ready.ValidatedThrough, ready.Ready.ValidatedHash),
            Is.EqualTo((32, 73UL, 1UL, 10UL, AnchorHash)));
    }

    [Test]
    public void Block_DoesNotAdvanceUntilExecutionCommits_AndRejectsWrongRoots()
    {
        StreamingSession session = Session();
        using SessionOperation operation = session.AdmitBlock(session.Binding, 7, Block(11, AnchorHash));
        Assert.That(session.Ready(session.Binding, 8).Ready.ValidatedThrough, Is.EqualTo(10));
        ExecutedStreamingBlock execution = Executed(operation) with { PreStateRoot = Hash(99) };
        AssertCode(() => session.CommitBlock(operation, execution), StatusCode.FailedPrecondition);
        Assert.That(session.Ready(session.Binding, 8).Ready.ValidatedThrough, Is.EqualTo(10));
        ServerFrame validated = session.CommitBlock(operation, Executed(operation));
        Assert.That((validated.RequestId, validated.Validated.Number, validated.Validated.PostStateRoot), Is.EqualTo((7UL, 11UL, Hash(31))));
        AssertCode(() => session.CommitBlock(operation, Executed(operation)), StatusCode.Aborted);
    }

    [Test]
    public void Block_AdmissionAndBackendCopiesCannotChangeAcceptedIdentity()
    {
        StreamingSession session = Session();
        BlockWitness block = Block(11, AnchorHash);
        using SessionOperation operation = session.AdmitBlock(session.Binding, 1, block);
        block.Hash = Hash(99);
        BlockWitness copy = operation.Block!;
        copy.Hash = Hash(98);
        copy.Witness.State.Clear();
        Assert.That(session.CommitBlock(operation, Executed(operation)).Validated.Hash, Is.EqualTo(Hash(11)));
    }

    [Test]
    public void Duplicate_ReexecutesWitness_WithoutAppending_AndRejectsChangedRlp()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        BlockWitness duplicate = Block(11, AnchorHash);
        duplicate.Witness = new ExecutionWitness();
        using (SessionOperation operation = session.AdmitBlock(session.Binding, 2, duplicate))
            session.CommitBlock(operation, Executed(operation));
        Assert.That(session.Ready(session.Binding, 3).Ready.ValidatedThrough, Is.EqualTo(11));
        duplicate.Rlp = ByteString.CopyFrom(new byte[] { 0xff });
        AssertCode(() => session.AdmitBlock(session.Binding, 4, duplicate), StatusCode.InvalidArgument);
        Assert.That(session.Ready(session.Binding, 6).Ready.ValidatedThrough, Is.EqualTo(11), "invalid encoding cannot replace the retained prefix");
        duplicate = Block(11, AnchorHash);
        duplicate.Hash = Hash(99);
        AssertCode(() => session.AdmitBlock(session.Binding, 7, duplicate), StatusCode.FailedPrecondition);
        duplicate = Block(11, AnchorHash);
        duplicate.ParentHash = Hash(99);
        AssertCode(() => session.AdmitBlock(session.Binding, 9, duplicate), StatusCode.InvalidArgument);
        Assert.That(session.Ready(session.Binding, 10).Ready.ValidatedThrough, Is.EqualTo(11), "contradictory duplicate metadata cannot replace validated data");
        using (SessionOperation retry = session.AdmitBlock(session.Binding, 8, Block(11, AnchorHash)))
            session.CommitBlock(retry, Executed(retry));
        Add(session, 12, Hash(11));
        using SessionOperation finalize = session.AdmitFinalize(session.Binding, 5, FinalizeRange(12, 12));
        Assert.That(finalize.CopyBlocks()[0].Number, Is.EqualTo(12));
    }

    [TestCase(true, TestName = "Duplicate_AtByteQuota_ReexecutesWithoutReservingAnotherBlock")]
    [TestCase(false, TestName = "Duplicate_AtWitnessQuota_ReexecutesWithoutReservingAnotherBlock")]
    public void Duplicate_AtFullQuota_ReexecutesAndKeepsTheRetainedBudget(bool byteQuota)
    {
        BlockWitness block = Block(11, AnchorHash);
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(2,
            byteQuota ? block.CalculateSize() : 10000, byteQuota ? 16 : 1));
        Add(session, 11, AnchorHash);
        Assert.That(session.Ready(session.Binding, 1).Ready.ValidatedThrough, Is.EqualTo(11), "precondition: the retained window fills its configured quota");
        using (SessionOperation duplicate = session.AdmitBlock(session.Binding, 2, block))
            Assert.That(session.CommitBlock(duplicate, Executed(duplicate)).Validated.Number, Is.EqualTo(11), "duplicate execution is verified without retaining a second copy");
        AssertCode(() => session.AdmitBlock(session.Binding, 3, Block(12, Hash(11))), StatusCode.ResourceExhausted);
        Assert.That(session.Ready(session.Binding, 4).Ready.ValidatedThrough, Is.EqualTo(11), "a duplicate does not create capacity or advance the prefix");
    }

    [Test]
    public void Duplicate_WithOversizedReplacementWitness_IsStillRejected([Values] bool byteQuota, [Values] bool conditional)
    {
        BlockWitness block = Block(11, AnchorHash);
        long maxBytes = byteQuota ? block.CalculateSize() : 10000;
        long maxWitnessItems = byteQuota ? 16 : 1;
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(2, maxBytes, maxWitnessItems),
            new StreamingProtocolRules(conditional ? StreamingSettlementProfile.Rollup0Conditional : StreamingSettlementProfile.Rollup0Calldata));
        Add(session, 11, AnchorHash);
        block.Witness.Codes.Add(Hash(90));
        long items = (long)block.Witness.State.Count + block.Witness.Codes.Count + block.Witness.Keys.Count + block.Witness.Headers.Count;
        using (Assert.EnterMultipleScope())
        {
            if (byteQuota)
            {
                Assert.That(block.CalculateSize(), Is.GreaterThan(maxBytes));
                Assert.That(items, Is.LessThanOrEqualTo(maxWitnessItems));
            }
            else
            {
                Assert.That(block.CalculateSize(), Is.LessThanOrEqualTo(maxBytes));
                Assert.That(items, Is.GreaterThan(maxWitnessItems));
            }
        }
        AssertCode(() => session.AdmitBlock(session.Binding, 2, block), StatusCode.ResourceExhausted);
        Assert.That(session.Ready(session.Binding, 3).Ready.ValidatedThrough, Is.EqualTo(11), "temporary duplicate witness data remains independently bounded");
    }

    [TestCase(0, TestName = "Rewind_EmptyHash_RejectsWithoutChangingBinding")]
    [TestCase(31, TestName = "Rewind_ShortHash_RejectsWithoutChangingBinding")]
    [TestCase(33, TestName = "Rewind_LongHash_RejectsWithoutChangingBinding")]
    public void Rewind_MalformedHash_RejectsWithoutChangingProgress(int size)
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        SessionBinding binding = session.Binding;
        AssertCode(() => session.Rewind(binding, 2, ByteString.CopyFrom(new byte[size])), StatusCode.InvalidArgument);
        Assert.That((session.Binding, session.Ready(binding, 3).Ready.ValidatedThrough), Is.EqualTo((binding, 11UL)), "malformed controls do not fence valid work or change the cursor");
    }

    [TestCase(0UL, TestName = "Finalize_ZeroStart_RejectsMalformedRange")]
    [TestCase(10UL, TestName = "Finalize_InitialAnchorStart_RejectsMalformedRange")]
    public void Finalize_BeforeInitialExecutableRange_RejectsWithoutConsumingAdmission(ulong from)
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        AssertCode(() => session.AdmitFinalize(session.Binding, 2, FinalizeRange(from, 11)), StatusCode.InvalidArgument);
        using SessionOperation retry = session.AdmitFinalize(session.Binding, 3, FinalizeRange(11, 11));
        Assert.That(retry.CopyBlocks()[0].Number, Is.EqualTo(11), "an invalid range does not prevent a valid proof attempt");
    }

    [Test]
    public void Resume_FencesOldAttachment_EvenWithNewEpoch_AndChecksOwner()
    {
        StreamingSession session = Session();
        SessionBinding old = session.Binding;
        SessionBinding current = session.Resume("composer");
        AssertCode(() => session.Ready(old with { Epoch = current.Epoch }, 1), StatusCode.Aborted);
        AssertCode(() => session.Resume("other"), StatusCode.NotFound);
        Assert.That(session.Ready(current, 1).Epoch, Is.EqualTo(2));
    }

    [Test]
    public void Rewind_FencesPendingExecution_AndKeepsWorkerSlotUntilDrain()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        using SessionOperation pending = session.AdmitBlock(session.Binding, 2, Block(12, Hash(11)));
        ServerFrame ready = session.Rewind(session.Binding, 3, AnchorHash);
        Assert.That((ready.Ready.ValidatedThrough, ready.Epoch), Is.EqualTo((10UL, 2UL)));
        AssertCode(() => session.CommitBlock(pending, Executed(pending)), StatusCode.Aborted);
        AssertCode(() => session.AdmitBlock(session.Binding, 4, Block(11, AnchorHash)), StatusCode.ResourceExhausted);
        pending.Dispose();
        Add(session, 11, AnchorHash);
    }

    [Test]
    public void InvalidRewind_DoesNotChangeEpochOrProgress()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        SessionBinding binding = session.Binding;
        AssertCode(() => session.Rewind(binding, 9, Hash(88)), StatusCode.FailedPrecondition);
        Assert.That((session.Binding, session.Ready(binding, 10).Ready.ValidatedThrough), Is.EqualTo((binding, 11UL)));
    }

    [Test]
    public void Finalize_SelectsSubrangeAndPredecessor_WithoutChangingProgress()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        Add(session, 12, Hash(11));
        Add(session, 13, Hash(12));
        Finalize finalize = FinalizeRange(12, 12);
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 4, finalize);
        finalize.Settlement.AbiCalldata = ByteString.Empty;
        BlockWitness[] copy = operation.CopyBlocks();
        copy[0].Hash = Hash(99);
        Assert.That((operation.CopyBlocks().Length, operation.CopyBlocks()[0].Hash, operation.Predecessor!.AnchorNumber,
            operation.Predecessor.AnchorStateRoot, session.Ready(session.Binding, 5).Ready.ValidatedThrough),
            Is.EqualTo((1, Hash(12), 11UL, Hash(31), 13UL)));
        Finalize finalizeCopy = operation.Finalize!;
        finalizeCopy.Settlement.AbiCalldata = ByteString.Empty;
        finalizeCopy.FromBlock = 99;
        finalizeCopy.ToBlock = 100;
        Assert.That(operation.IsFinalization, Is.True);
        Assert.That(operation.FinalizationRange, Is.EqualTo((12UL, 12UL)));
        Begin predecessorCopy = operation.Predecessor!;
        predecessorCopy.AnchorHash = Hash(98);
        Assert.That(operation.Finalize!.Settlement.AbiCalldata, Is.EqualTo(Hash(70)));
        Assert.That(operation.Predecessor!.AnchorHash, Is.EqualTo(Hash(11)));
    }

    [Test]
    public void Proof_PromotesExactPredecessor_AndPreservesLaterValidatedBlocks()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        Add(session, 12, Hash(11));
        Add(session, 13, Hash(12));
        using (SessionOperation operation = session.AdmitFinalize(session.Binding, 4, FinalizeRange(12, 12)))
        {
            ServerFrame proof = session.CompleteProof(operation, new Proof { PublicInputsHash = Hash(80), Proof_ = Hash(81) });
            Assert.That((proof.Epoch, proof.RequestId), Is.EqualTo((1UL, 4UL)));
        }
        SessionBinding resumed = session.Resume("composer");
        Assert.That(session.Ready(resumed, 5).Ready.ValidatedThrough, Is.EqualTo(13));
        Assert.That(session.Rewind(resumed, 6, Hash(11)).Ready.ValidatedThrough, Is.EqualTo(11));
        AssertCode(() => session.Rewind(session.Binding, 7, AnchorHash), StatusCode.FailedPrecondition);
    }

    [Test]
    public void Cancel_FencesPendingProof_AndRetiresSession()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 2, FinalizeRange(11, 11));
        ServerFrame cancelled = session.Cancel(session.Binding, 3);
        Assert.That((cancelled.Epoch, cancelled.RequestId, cancelled.KindCase), Is.EqualTo((1UL, 3UL, ServerFrame.KindOneofCase.Cancelled)));
        AssertCode(() => session.CompleteProof(operation, new Proof { PublicInputsHash = Hash(80), Proof_ = Hash(81) }), StatusCode.Aborted);
        AssertCode(() => session.Resume("composer"), StatusCode.NotFound);
    }

    [Test]
    public void FailedExecutionAndFinalize_ReleaseAdmissionForRetry()
    {
        StreamingSession session = Session();
        using (SessionOperation operation = session.AdmitBlock(session.Binding, 1, Block(11, AnchorHash))) { }
        Add(session, 11, AnchorHash);
        using (SessionOperation operation = session.AdmitFinalize(session.Binding, 2, FinalizeRange(11, 11)))
            AssertCode(() => session.CompleteProof(operation, new Proof()), StatusCode.Internal);
        using SessionOperation retry = session.AdmitFinalize(session.Binding, 3, FinalizeRange(11, 11));
        Assert.That(retry.CopyBlocks()[0].Number, Is.EqualTo(11));
    }

    [Test]
    public void Quota_RejectionDoesNotAdvance_AndRewindReleasesCapacity()
    {
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(1, 1000, 16));
        Add(session, 11, AnchorHash);
        AssertCode(() => session.AdmitBlock(session.Binding, 2, Block(12, Hash(11))), StatusCode.ResourceExhausted);
        session.Rewind(session.Binding, 3, AnchorHash);
        Add(session, 11, AnchorHash);
        Assert.That(session.Ready(session.Binding, 4).Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [Test]
    public void MalformedRequests_DoNotAdvanceOrConsumeWorkerSlot()
    {
        StreamingSession session = Session();
        BlockWitness malformed = Block(11, AnchorHash);
        malformed.Witness = null;
        AssertCode(() => session.AdmitBlock(session.Binding, 1, malformed), StatusCode.InvalidArgument);
        AssertCode(() => session.AdmitBlock(session.Binding, 2, Block(12, AnchorHash)), StatusCode.FailedPrecondition);
        AssertCode(() => session.AdmitFinalize(session.Binding, 3, FinalizeRange(11, 11)), StatusCode.FailedPrecondition);
        Add(session, 11, AnchorHash);
        Assert.That(session.Ready(session.Binding, 4).Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [TestCase("number")]
    [TestCase("hash")]
    [TestCase("parent")]
    [TestCase("postroot")]
    public void ExecutionIdentityMismatch_DoesNotAdvance_AndAllowsCorrectRetry(string mismatch)
    {
        StreamingSession session = Session();
        using SessionOperation operation = session.AdmitBlock(session.Binding, 1, Block(11, AnchorHash));
        ExecutedStreamingBlock valid = Executed(operation);
        ExecutedStreamingBlock invalid = mismatch switch
        {
            "number" => valid with { Number = 12 },
            "hash" => valid with { Hash = Hash(90) },
            "parent" => valid with { ParentHash = Hash(91) },
            _ => valid with { PostStateRoot = ByteString.Empty },
        };
        AssertCode(() => session.CommitBlock(operation, invalid), StatusCode.FailedPrecondition);
        Assert.That(session.Ready(session.Binding, 2).Ready.ValidatedThrough, Is.EqualTo(10));
        Assert.That(session.CommitBlock(operation, valid).Validated.Number, Is.EqualTo(11));
    }

    [Test]
    public void DuplicateExecution_WithDifferentPostRoot_IsRejected()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        using SessionOperation duplicate = session.AdmitBlock(session.Binding, 2, Block(11, AnchorHash));
        AssertCode(() => session.CommitBlock(duplicate, Executed(duplicate) with { PostStateRoot = Hash(90) }), StatusCode.FailedPrecondition);
        Assert.That(session.CommitBlock(duplicate, Executed(duplicate)).Validated.PostStateRoot, Is.EqualTo(Hash(31)));
    }

    [Test]
    public void ByteQuota_AtLimitSucceeds_OneOverIsRejected()
    {
        BlockWitness block = Block(11, AnchorHash);
        StreamingSession exact = new("composer", Anchor(), new StreamingSessionLimits(8, block.CalculateSize(), 16));
        Add(exact, 11, AnchorHash);
        StreamingSession small = new("composer", Anchor(), new StreamingSessionLimits(8, block.CalculateSize() - 1, 16));
        AssertCode(() => small.AdmitBlock(small.Binding, 1, block), StatusCode.ResourceExhausted);
        Assert.That(small.Ready(small.Binding, 2).Ready.ValidatedThrough, Is.EqualTo(10));
    }

    [Test]
    public void WitnessQuota_AtLimitSucceeds_OneOverIsRejected()
    {
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(8, 10000, 1));
        BlockWitness oversized = Block(11, AnchorHash);
        oversized.Witness.Codes.Add(Hash(90));
        AssertCode(() => session.AdmitBlock(session.Binding, 1, oversized), StatusCode.ResourceExhausted);
        Add(session, 11, AnchorHash);
        AssertCode(() => session.AdmitBlock(session.Binding, 2, Block(12, Hash(11))), StatusCode.ResourceExhausted);
        Assert.That(session.Ready(session.Binding, 3).Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [Test]
    public void Finalize_FullRetainedByteQuota_UsesIndependentSettlementBudget()
    {
        BlockWitness block = Block(11, AnchorHash);
        Finalize finalize = FinalizeRange(11, 11);
        Assert.That(finalize.CalculateSize(), Is.LessThanOrEqualTo(block.CalculateSize()), "settlement fits its independent byte limit");
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(8, block.CalculateSize(), 16));
        Add(session, 11, AnchorHash);
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 2, finalize);
        Assert.That(operation.CopyBlocks()[0].Number, Is.EqualTo(11), "a full retained window remains finalizable");
    }

    [Test]
    public void SettlementQuota_AtLimitSucceeds_OneOverIsRejected()
    {
        Finalize finalize = FinalizeRange(11, 11);
        finalize.Settlement.AbiCalldata = ByteString.CopyFrom(new byte[512]);
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(8, finalize.CalculateSize(), 16));
        Add(session, 11, AnchorHash);
        using (SessionOperation admitted = session.AdmitFinalize(session.Binding, 2, finalize)) { }
        finalize.Settlement.AbiCalldata = ByteString.CopyFrom(new byte[513]);
        AssertCode(() => session.AdmitFinalize(session.Binding, 3, finalize), StatusCode.ResourceExhausted);
        Assert.That(session.Ready(session.Binding, 4).Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [Test]
    public void StaleAttachment_CannotAdmitBlocksFinalizeRewindOrCancel()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        SessionBinding old = session.Binding;
        SessionBinding current = session.Resume("composer");
        AssertCode(() => session.AdmitBlock(old, 2, Block(12, Hash(11))), StatusCode.Aborted);
        AssertCode(() => session.AdmitFinalize(old, 3, FinalizeRange(11, 11)), StatusCode.Aborted);
        AssertCode(() => session.Rewind(old, 4, AnchorHash), StatusCode.Aborted);
        AssertCode(() => session.Cancel(old, 5), StatusCode.Aborted);
        Assert.That(session.Ready(current, 6).Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [Test]
    public void Finalize_MismatchedTerminalHash_IsRejectedWithoutLosingProgress()
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        Finalize request = FinalizeRange(11, 11);
        request.TerminalHash = Hash(88);
        AssertCode(() => session.AdmitFinalize(session.Binding, 2, request), StatusCode.FailedPrecondition);
        using SessionOperation retry = session.AdmitFinalize(session.Binding, 3, FinalizeRange(11, 11));
        Assert.That(session.Ready(session.Binding, 4).Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [TestCase("rollup")]
    [TestCase("number")]
    [TestCase("hash")]
    [TestCase("root")]
    public void Begin_InvalidAnchor_IsRejected(string invalid)
    {
        Begin begin = Anchor();
        switch (invalid)
        {
            case "rollup": begin.RollupId = 0; break;
            case "number": begin.AnchorNumber = ulong.MaxValue; break;
            case "hash": begin.AnchorHash = ByteString.Empty; break;
            default: begin.AnchorStateRoot = ByteString.Empty; break;
        }
        AssertCode(() => new StreamingSession("composer", begin, Limits()), StatusCode.InvalidArgument);
    }

    [TestCase("rlp", StatusCode.FailedPrecondition)]
    [TestCase("hash", StatusCode.InvalidArgument)]
    [TestCase("parentLength", StatusCode.InvalidArgument)]
    [TestCase("parent", StatusCode.FailedPrecondition)]
    public void Block_InvalidShapeOrParent_IsRejected(string invalid, StatusCode code)
    {
        StreamingSession session = Session();
        BlockWitness block = Block(11, AnchorHash);
        switch (invalid)
        {
            case "rlp": block.Rlp = ByteString.Empty; break;
            case "hash": block.Hash = ByteString.Empty; break;
            case "parentLength": block.ParentHash = ByteString.Empty; break;
            default: block.ParentHash = Hash(90); break;
        }
        AssertCode(() => session.AdmitBlock(session.Binding, 1, block), code);
        Add(session, 11, AnchorHash);
    }

    [TestCase("keys")]
    [TestCase("headers")]
    public void WitnessQuota_CountsKeysAndHeaders(string collection)
    {
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(8, 10000, 1));
        BlockWitness oversized = Block(11, AnchorHash);
        if (collection == "keys") oversized.Witness.Keys.Add(Hash(90));
        else oversized.Witness.Headers.Add(Hash(90));
        AssertCode(() => session.AdmitBlock(session.Binding, 1, oversized), StatusCode.ResourceExhausted);
        Add(session, 11, AnchorHash);
    }

    [TestCase("settlement")]
    [TestCase("hash")]
    [TestCase("bounds")]
    public void Finalize_InvalidShape_IsRejected(string invalid)
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        Finalize finalize = FinalizeRange(11, 11);
        switch (invalid)
        {
            case "settlement": finalize.Settlement = null; break;
            case "hash": finalize.TerminalHash = ByteString.Empty; break;
            default: finalize.FromBlock = 12; break;
        }
        AssertCode(() => session.AdmitFinalize(session.Binding, 2, finalize), StatusCode.InvalidArgument);
        using SessionOperation retry = session.AdmitFinalize(session.Binding, 3, FinalizeRange(11, 11));
    }

    [TestCase("hash")]
    [TestCase("proof")]
    public void Proof_InvalidResult_IsRejectedWithoutChangingProgress(string invalid)
    {
        StreamingSession session = Session();
        Add(session, 11, AnchorHash);
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 2, FinalizeRange(11, 11));
        Proof proof = new() { PublicInputsHash = Hash(80), Proof_ = Hash(81) };
        if (invalid == "hash") proof.PublicInputsHash = ByteString.Empty;
        else proof.Proof_ = ByteString.Empty;
        AssertCode(() => session.CompleteProof(operation, proof), StatusCode.Internal);
        Assert.That(session.Ready(session.Binding, 3).Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [Test]
    public void Wire_KnownLiteralBytes_KeepPinnedFieldNumbers()
    {
        ClientFrame begin = ClientFrame.Parser.ParseFrom(new byte[] { 0x10, 0x07, 0x22, 0x02, 0x08, 0x01 });
        Assert.That((begin.RequestId, begin.Begin.RollupId, begin.KindCase), Is.EqualTo((7UL, 1UL, ClientFrame.KindOneofCase.Begin)));
        Assert.That(begin.ToByteArray(), Is.EqualTo(new byte[] { 0x10, 0x07, 0x22, 0x02, 0x08, 0x01 }));
        ServerFrame cancelled = ServerFrame.Parser.ParseFrom(new byte[] { 0x10, 0x09, 0x18, 0x02, 0x3a, 0x00 });
        Assert.That((cancelled.RequestId, cancelled.Epoch, cancelled.KindCase), Is.EqualTo((9UL, 2UL, ServerFrame.KindOneofCase.Cancelled)));
    }

    [Test]
    public void Finalize_RollupXAggregateWireQuota_AcceptsExactLimitAndRejectsOneByteOver()
    {
        ClientFrame begin = new() { RequestId = 1, Begin = Anchor() };
        ClientFrame block = new() { SessionId = Hash(90), Epoch = 1, RequestId = 2, Block = new Nethermind.Eez.Prove.V2.Block { Data = Block(11, AnchorHash) } };
        ClientFrame finalization = new() { SessionId = Hash(90), Epoch = 1, RequestId = 3, Finalize = FinalizeRange(11, 11) };
        long limit = begin.CalculateSize() + block.CalculateSize() + finalization.CalculateSize();
        StreamingSession session = new("composer", begin.Begin, new StreamingSessionLimits(8, limit, 16),
            new StreamingProtocolRules(StreamingSettlementProfile.RollupXNative), begin.CalculateSize());
        using (SessionOperation append = session.AdmitBlock(session.Binding, 2, block.Block.Data, block.CalculateSize()))
            session.CommitBlock(append, Executed(append));
        using (SessionOperation exact = session.AdmitFinalize(session.Binding, 3, finalization.Finalize, finalization.CalculateSize()))
            Assert.That(exact.CopyBlocks()[0].Number, Is.EqualTo(11));
        finalization.Finalize.Settlement.AbiCalldata = ByteString.CopyFrom([.. finalization.Finalize.Settlement.AbiCalldata.Span, 1]);
        AssertCode(() => session.AdmitFinalize(session.Binding, 4, finalization.Finalize, finalization.CalculateSize()), StatusCode.ResourceExhausted);
        Assert.That(session.Ready(session.Binding, 5).Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [TestCase((int)StreamingSettlementProfile.Rollup0Calldata, StatusCode.InvalidArgument, TestName = "Rewind_Rollup0MalformedAncestor_IsInvalidArgument")]
    [TestCase((int)StreamingSettlementProfile.RollupXNative, StatusCode.FailedPrecondition, TestName = "Rewind_RollupXMalformedAncestor_IsFailedPrecondition")]
    public void Rewind_MalformedAncestor_UsesSelectedRulesWithoutChangingPrefix(int profileValue, StatusCode expected)
    {
        StreamingSettlementProfile profile = (StreamingSettlementProfile)profileValue;
        StreamingSession session = new("composer", Anchor(), Limits(), new StreamingProtocolRules(profile), 74);
        SessionBinding binding = session.Binding;
        AssertCode(() => session.Rewind(binding, 2, ByteString.Empty), expected);
        Assert.That((session.Binding, session.Ready(binding, 3).Ready.ValidatedThrough), Is.EqualTo((binding, 10UL)));
    }

    [Test]
    public void ConditionalQuota_ConsecutiveFullWindows_KeepSessionAndBoundEachProof([Values("blocks", "bytes", "items")] string quota)
    {
        long pairBytes = Block(11, AnchorHash).CalculateSize() + Block(12, Hash(11)).CalculateSize();
        StreamingSessionLimits limits = new(quota == "blocks" ? 2 : 8, quota == "bytes" ? pairBytes : 10000, quota == "items" ? 2 : 16);
        StreamingSession session = new("composer", Anchor(), limits, new StreamingProtocolRules(StreamingSettlementProfile.Rollup0Conditional));
        for (ulong first = 11; first < 17; first += 2)
        {
            Add(session, first, first == 11 ? AnchorHash : Hash((byte)(first - 1)));
            Add(session, first + 1, Hash((byte)first));
            AssertCode(() => session.AdmitBlock(session.Binding, 1, Block(first + 2, Hash((byte)(first + 1)))), StatusCode.ResourceExhausted);
            if (first > 11)
                AssertCode(() => session.AdmitFinalize(session.Binding, 2, FinalizeRange(first - 2, first + 1)), StatusCode.ResourceExhausted);
            Complete(session, first, first + 1);
            Assert.That(session.Ready(session.Binding, 3).Ready.ValidatedThrough, Is.EqualTo(first + 1));
        }
        AssertCode(() => session.AdmitFinalize(session.Binding, 4, FinalizeRange(11, 12)), StatusCode.OutOfRange);
    }

    [Test]
    public void ConditionalQuota_ProofRetryAndRewind_DoNotGrantAnotherWindow()
    {
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(2, 10000, 16),
            new StreamingProtocolRules(StreamingSettlementProfile.Rollup0Conditional));
        Add(session, 11, AnchorHash);
        Add(session, 12, Hash(11));
        Complete(session, 11, 12);
        Add(session, 13, Hash(12));
        Complete(session, 11, 11);
        Add(session, 14, Hash(13));
        AssertCode(() => session.AdmitBlock(session.Binding, 1, Block(15, Hash(14))), StatusCode.ResourceExhausted);
        using (SessionOperation duplicate = session.AdmitBlock(session.Binding, 2, Block(14, Hash(13))))
            session.CommitBlock(duplicate, Executed(duplicate));
        AssertCode(() => session.AdmitBlock(session.Binding, 3, Block(15, Hash(14))), StatusCode.ResourceExhausted);

        session.Rewind(session.Binding, 4, Hash(11));
        Add(session, 12, Hash(11));
        Add(session, 13, Hash(12));
        AssertCode(() => session.AdmitBlock(session.Binding, 5, Block(14, Hash(13))), StatusCode.ResourceExhausted);
        session.Rewind(session.Binding, 6, AnchorHash);
        Add(session, 11, AnchorHash);
        Add(session, 12, Hash(11));
        AssertCode(() => session.AdmitBlock(session.Binding, 7, Block(13, Hash(12))), StatusCode.ResourceExhausted);
    }

    [Test]
    public void ConditionalQuota_FailedProofAndStaleProof_DoNotReleaseActiveBudget([Values] bool stale)
    {
        StreamingSession session = new("composer", Anchor(), new StreamingSessionLimits(1, 10000, 16),
            new StreamingProtocolRules(StreamingSettlementProfile.Rollup0Conditional));
        Add(session, 11, AnchorHash);
        using (SessionOperation operation = session.AdmitFinalize(session.Binding, 1, FinalizeRange(11, 11)))
        {
            if (stale) session.Resume("composer");
            AssertCode(() => session.CompleteProof(operation, stale ? ValidProof() : new Proof()), stale ? StatusCode.Aborted : StatusCode.Internal);
        }
        AssertCode(() => session.AdmitBlock(session.Binding, 2, Block(12, Hash(11))), StatusCode.ResourceExhausted);
        Complete(session, 11, 11);
        Add(session, 12, Hash(11));
    }

    [TestCase(0UL, 12UL, StatusCode.InvalidArgument)]
    [TestCase(12UL, 11UL, StatusCode.InvalidArgument)]
    [TestCase(10UL, 9UL, StatusCode.InvalidArgument)]
    [TestCase(10UL, 12UL, StatusCode.OutOfRange)]
    [TestCase(11UL, 13UL, StatusCode.FailedPrecondition)]
    public void ConditionalFinalize_InvalidRange_UsesRecoveryStatusWithoutChangingProgress(ulong from, ulong to, StatusCode expected)
    {
        StreamingSession session = new("composer", Anchor(), Limits(), new StreamingProtocolRules(StreamingSettlementProfile.Rollup0Conditional));
        Add(session, 11, AnchorHash);
        Add(session, 12, Hash(11));
        SessionBinding binding = session.Binding;
        AssertCode(() => session.AdmitFinalize(binding, 1, FinalizeRange(from, to)), expected);
        Assert.That((session.Binding, session.Ready(binding, 2).Ready.ValidatedThrough), Is.EqualTo((binding, 12UL)));
    }

    private static Proof ValidProof() => new() { PublicInputsHash = Hash(80), Proof_ = Hash(81) };

    private static void Complete(StreamingSession session, ulong from, ulong to)
    {
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 1, FinalizeRange(from, to));
        session.CompleteProof(operation, ValidProof());
    }

    private static StreamingSession Session() => new("composer", Anchor(), Limits());
    private static StreamingSessionLimits Limits() => new(8, 10000, 16);
    private static Begin Anchor() => new() { RollupId = 1, AnchorNumber = 10, AnchorHash = AnchorHash, AnchorStateRoot = AnchorRoot };
    private static ByteString Hash(byte value)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(value);
        return ByteString.CopyFrom(bytes);
    }
    private static BlockWitness Block(ulong number, ByteString parent) => new()
    {
        Number = number,
        Hash = Hash((byte)number),
        ParentHash = parent,
        Rlp = Hash((byte)number),
        Witness = new ExecutionWitness { State = { Hash(60) } },
    };
    private static Finalize FinalizeRange(ulong from, ulong to) => new()
    {
        FromBlock = from,
        ToBlock = to,
        TerminalHash = Hash((byte)to),
        Settlement = new SettlementInput { AbiCalldata = Hash(70) },
    };
    private static ExecutedStreamingBlock Executed(SessionOperation operation)
    {
        BlockWitness block = operation.Block!;
        return new(block.Number, block.Hash, block.ParentHash, operation.PreStateRoot, Hash((byte)(block.Number + 20)));
    }
    private static void Add(StreamingSession session, ulong number, ByteString parent)
    {
        using SessionOperation operation = session.AdmitBlock(session.Binding, number, Block(number, parent));
        session.CommitBlock(operation, Executed(operation));
    }
    private static void AssertCode(Action action, StatusCode code) => Assert.That(Assert.Throws<RpcException>(action)!.StatusCode, Is.EqualTo(code));
}
