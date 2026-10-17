// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove.V2;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXParentAuthorizerTests
{
    private Evidence _source = null!;
    private HttpClient _l1 = null!;
    private HttpClient _l2 = null!;

    [SetUp]
    public void SetUp()
    {
        _source = new Evidence();
        _l1 = new HttpClient(new Handler(_source, false)) { BaseAddress = new Uri("http://l1.invalid") };
        _l2 = new HttpClient(new Handler(_source, true)) { BaseAddress = new Uri("http://l2.invalid") };
    }

    [TearDown]
    public void TearDown() { _l1.Dispose(); _l2.Dispose(); }

    [TestCase(false, TestName = "Authorize_LiveAcceptedParent_RefreshesDescendantSeed")]
    [TestCase(true, TestName = "Authorize_LaterCatchUpAcceptance_KeepsEarlierLiveSeed")]
    public async Task Authorize_CanonicalLiveSlot_AuthenticatesParentTargetAndSeed(bool later)
    {
        if (later) _source.AddBatch(104, 0, _source.Parent.Hash!);
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ParentHeader.ToByteArray(), Is.EqualTo(Rlp.Encode(_source.Parent).Bytes), "the owned parent is the exact selected raw header");
            Assert.That((result.L1ParentNumber, result.Target.Timestamp, result.Target.ParentHash, result.DescendantSeed),
                Is.EqualTo((105UL, 1030UL, _source.Blocks[105].Hash, _source.Blocks[101].MixHash)), "a live endpoint refreshes descendants even when a later catch-up repeats it");
            Assert.That(result.RegistryRoot, Is.EqualTo(_source.RegistryRoot), "registry root is returned as evidence without approving a consumed prefix");
            Assert.That(_source.Calls, Is.EqualTo(4), "registration, vkey, signer and threshold are all checked at the named target");
            Assert.That(_source.BlockReads.All(static n => n is >= 100 and <= 105), Is.True, "a canonical target behind tip is supported without latest reads");
        }
    }

    [Test]
    public async Task Authorize_CatchUpWithoutLiveSettlement_PreservesParentSeed()
    {
        _source.ClearBatch(101);
        _source.AddBatch(104, 0, _source.Parent.Hash!);
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        Assert.That(result.DescendantSeed, Is.EqualTo(_source.Parent.MixHash), "a later catch-up cannot replace the predecessor's original seed");
    }

    [Test]
    public async Task Authorize_ExactConfiguredGenesis_DoesNotSearchSettlementHistory()
    {
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(_source.Genesis), _source.Target(), CancellationToken.None);
        Assert.That((result.DescendantSeed, _source.LogReads), Is.EqualTo((_source.Genesis.MixHash, 0)), "trusted genesis has no claimed acceptance or live refresh");
    }

    [TestCase(100UL, TestName = "Authorize_ReferenceAtFinalizedHeight_Authenticates")]
    [TestCase(105UL, TestName = "Authorize_ReferenceBelowFinalizedHeight_Authenticates")]
    public async Task Authorize_FinalizedReference_ReadsFinalityBeforeCanonicalReference(ulong height)
    {
        _source.Finalized = _source.Blocks[height];
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CanonicalEvidence[0], Is.EqualTo(_source.Blocks[100]), "canonical reference identity remains part of the signing recheck");
            Assert.That(_source.HeaderReads.Take(2), Is.EqualTo(new[] { "finalized", "0x64" }), "the reference is read canonically after finality is established");
            Assert.That(_source.HeaderReads.Count(static selector => selector == "finalized"), Is.EqualTo(1), "one bounded finality read covers each authorization");
        }
    }

    [TestCase("height", StatusCode.FailedPrecondition, TestName = "Authorize_ReferenceAboveFinalizedHeight_Refuses")]
    [TestCase("timestamp", StatusCode.FailedPrecondition, TestName = "Authorize_FinalizedTimestampBeforeReference_Refuses")]
    [TestCase("hash", StatusCode.FailedPrecondition, TestName = "Authorize_FinalizedReferenceHashConflict_Refuses")]
    [TestCase("missing", StatusCode.Unavailable, TestName = "Authorize_FinalizedSourceMissing_IsUnavailable")]
    [TestCase("zeroHash", StatusCode.Unavailable, TestName = "Authorize_FinalizedHashZero_IsUnavailable")]
    [TestCase("numberEncoding", StatusCode.Unavailable, TestName = "Authorize_FinalizedHeightMalformed_IsUnavailable")]
    public async Task Authorize_InvalidFinality_DoesNotReturnContext(string mutation, StatusCode status)
    {
        _source.Mutation = "finalized" + mutation;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That((exception.StatusCode, exception.Trailers.Count, _source.LogReads, _source.Calls), Is.EqualTo((status, 0, 0, 0)),
            "invalid finality stops before parent history or deployment authorization and exposes no eviction details");
    }

    [TestCase(false, TestName = "Authorize_CanonicalBlockZero_DoesNotRequireFinalizedCheckpoint")]
    [TestCase(true, TestName = "Authorize_ReplacedBlockZero_RefusesWithoutFinalizedRead")]
    public async Task Authorize_ReferenceAtBlockZero_AuthenticatesCanonicalGenesis(bool replaced)
    {
        CanonicalL1Header reference = _source.Blocks[100] with { Number = 0 };
        _source.Blocks[0] = reference;
        _source.Schedule = _source.Schedule with { L1Reference = _source.Schedule.L1Reference with { Number = 0 } };
        _source.Mutation = "finalizedmissing";
        if (replaced) _source.Blocks[0] = reference with { Hash = TestItem.KeccakC };
        if (replaced)
        {
            RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(_source.Genesis), _source.Target(), CancellationToken.None)))!;
            Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition), "block zero must match the exact configured reference even without a finalized checkpoint");
        }
        else
        {
            RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(_source.Genesis), _source.Target(), CancellationToken.None);
            Assert.That(result.CanonicalEvidence[0], Is.EqualTo(reference), "block zero is authenticated canonically and retained for recheck");
        }
        Assert.That(_source.HeaderReads, Does.Not.Contain("finalized"), "block zero is final by definition on a fresh private devnet");
    }

    [TestCase("rollup", StatusCode.FailedPrecondition, TestName = "Authorize_WrongRollup_Refuses")]
    [TestCase("number", StatusCode.FailedPrecondition, TestName = "Authorize_WrongParentNumber_Refuses")]
    [TestCase("root", StatusCode.FailedPrecondition, TestName = "Authorize_WrongParentStateRoot_Refuses")]
    [TestCase("targetchain", StatusCode.FailedPrecondition, TestName = "Authorize_WrongTargetChain_Refuses")]
    [TestCase("targethash", StatusCode.Aborted, TestName = "Authorize_ReplacedTarget_Aborts")]
    [TestCase("targettime", StatusCode.FailedPrecondition, TestName = "Authorize_TargetNotAfterParent_Refuses")]
    [TestCase("targetphase", StatusCode.FailedPrecondition, TestName = "Authorize_TargetOffSlotGrid_Refuses")]
    [TestCase("targetbefore", StatusCode.FailedPrecondition, TestName = "Authorize_TargetBeforeReference_Refuses")]
    [TestCase("l1chain", StatusCode.FailedPrecondition, TestName = "Authorize_WrongOwnedL1Chain_Refuses")]
    [TestCase("l2chain", StatusCode.FailedPrecondition, TestName = "Authorize_WrongOwnedL2Chain_Refuses")]
    [TestCase("reference", StatusCode.FailedPrecondition, TestName = "Authorize_WrongConfiguredReference_Refuses")]
    [TestCase("referenceSeed", StatusCode.FailedPrecondition, TestName = "Authorize_WrongReferenceSeed_Refuses")]
    [TestCase("manager", StatusCode.FailedPrecondition, TestName = "Authorize_WrongRegisteredManager_Refuses")]
    [TestCase("padding", StatusCode.FailedPrecondition, TestName = "Authorize_PaddedManagerWord_Refuses")]
    [TestCase("zeroRoot", StatusCode.FailedPrecondition, TestName = "Authorize_UnregisteredRoot_Refuses")]
    [TestCase("vkey", StatusCode.FailedPrecondition, TestName = "Authorize_WrongVerificationKey_Refuses")]
    [TestCase("signer", StatusCode.FailedPrecondition, TestName = "Authorize_WrongSigner_Refuses")]
    [TestCase("threshold", StatusCode.FailedPrecondition, TestName = "Authorize_UnmetThreshold_Refuses")]
    [TestCase("rawtrailing", StatusCode.Unavailable, TestName = "Authorize_NoncanonicalOwnedHeader_IsUnavailable")]
    [TestCase("rawhash", StatusCode.Unavailable, TestName = "Authorize_WrongOwnedHeaderHash_IsUnavailable")]
    [TestCase("height", StatusCode.Unavailable, TestName = "Authorize_WrongReturnedL1Height_IsUnavailable")]
    [TestCase("missing", StatusCode.Unavailable, TestName = "Authorize_MissingOwnedEvidence_IsUnavailable")]
    [TestCase("http", StatusCode.Unavailable, TestName = "Authorize_OwnedSourceHttpFailure_IsUnavailable")]
    public async Task Authorize_InvalidClaimOrEvidence_ReturnsBoundedFailure(string mutation, StatusCode status)
    {
        Begin begin = _source.Begin();
        L1PostingContext target = _source.Target();
        switch (mutation)
        {
            case "rollup": begin.RollupId++; break;
            case "number": begin.AnchorNumber++; break;
            case "root": begin.AnchorStateRoot = ByteString.CopyFrom(TestItem.KeccakC.Bytes); break;
            case "targetchain": target.ChainId++; break;
            case "targethash": target.ParentHash = ByteString.CopyFrom(TestItem.KeccakC.Bytes); break;
            case "targettime": target.TargetTimestamp = 1025; break;
            case "targetphase": target.TargetTimestamp = 1031; break;
            case "targetbefore": target.ParentNumber = 99; break;
            default: _source.Mutation = mutation; break;
        }
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(begin, target, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, exception.Trailers.Count), Is.EqualTo((status, 0)), "authorization refuses with no transaction-eviction details");
    }

    [TestCase("gas", TestName = "Authorize_ParentWrongGasLimit_Refuses")]
    [TestCase("time", TestName = "Authorize_ParentWrongGrid_Refuses")]
    [TestCase("sync", TestName = "Authorize_ParentNotSyncPosition_Refuses")]
    [TestCase("extra", TestName = "Authorize_ParentLongExtraData_Refuses")]
    public async Task Authorize_AcceptedHeaderOutsideProfile_Refuses(string mutation)
    {
        BlockHeader parent = _source.Parent;
        if (mutation == "gas") parent.GasLimit--;
        if (mutation == "time") parent.Timestamp++;
        if (mutation == "sync") { parent.Number = 4; parent.Timestamp = 1004; }
        if (mutation == "extra") parent.ExtraData = new byte[33];
        _source.ReplaceParent(parent);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition), "L1 root presence cannot excuse an invalid endpoint header");
    }

    [Test]
    public async Task Authorize_DifferentHeaderWithSameStateAndRegistryRoot_DoesNotInventAcceptance()
    {
        BlockHeader sibling = Evidence.Header(5, 1005, TestItem.KeccakA, [0xBB]);
        Assert.That((sibling.StateRoot, sibling.Hash == _source.Parent.Hash), Is.EqualTo((_source.Parent.StateRoot, false)), "precondition: the same state has a different block hash");
        _source.Headers[sibling.Hash!] = sibling;
        _source.ClearBatch(101);
        _source.AddBatch(101, 0, sibling.Hash!);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unavailable), "a registry match and equal state do not prove selected-parent acceptance");
    }

    [Test]
    public async Task Authorize_IntermediateEmittedRoot_IsNotAnAcceptedTerminalEndpoint()
    {
        BlockHeader later = Evidence.Header(10, 1010, TestItem.KeccakA, [0xCC]);
        _source.Headers[later.Hash!] = later;
        _source.ClearBatch(101);
        _source.AddBatch(104, 0, _source.Parent.Hash!, later.Hash!);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unavailable), "an intermediate root log cannot substitute for a terminal accepted endpoint");
    }

    [Test]
    public async Task Authorize_InvalidEndpointAfterSeedMatch_StillRefusesCompleteSlot()
    {
        BlockHeader future = Evidence.Header(10, 1010, TestItem.KeccakA, [0xDD]);
        _source.Headers[future.Hash!] = future;
        _source.AddBatch(101, 1, future.Hash!);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition), "the whole live-slot endpoint set is validated after a matching seed endpoint");
    }

    [TestCase("removed", TestName = "Authorize_RemovedRootLog_IsUnavailable")]
    [TestCase("logaddress", TestName = "Authorize_WrongLogAddress_IsUnavailable")]
    [TestCase("logtopic", TestName = "Authorize_WrongLogTopic_IsUnavailable")]
    [TestCase("rootdata", TestName = "Authorize_TruncatedRootData_IsUnavailable")]
    [TestCase("duplicate", TestName = "Authorize_DuplicateLogPosition_IsUnavailable")]
    [TestCase("wrapper", TestName = "Authorize_WrapperOriginBatch_IsUnavailable")]
    [TestCase("badabi", TestName = "Authorize_UndecodableBatch_IsUnavailable")]
    [TestCase("batchdata", TestName = "Authorize_InconsistentBatchEvent_IsUnavailable")]
    public async Task Authorize_IncompleteSettlementAttribution_DoesNotReturnContext(string mutation)
    {
        _source.Mutation = mutation;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unavailable), "missing complete settlement evidence must not leave an earlier window open");
    }

    [TestCase("logfork", TestName = "Authorize_WrongForkLog_Aborts")]
    [TestCase("txhash", TestName = "Authorize_ReplacedBatchTransaction_Aborts")]
    [TestCase("txindex", TestName = "Authorize_WrongBatchTransactionPosition_Aborts")]
    [TestCase("txblock", TestName = "Authorize_WrongBatchTransactionBlock_Aborts")]
    public async Task Authorize_ReplacedCanonicalSettlement_Aborts(string mutation)
    {
        _source.Mutation = mutation;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Aborted), "all canonical log and transaction identities must agree");
    }

    [TestCase(100UL, TestName = "Recheck_ReplacedReference_Aborts")]
    [TestCase(101UL, TestName = "Recheck_ReplacedAcceptedLiveSlot_Aborts")]
    [TestCase(105UL, TestName = "Recheck_ReplacedNamedTarget_Aborts")]
    public async Task Recheck_CanonicalEvidenceReplaced_Aborts(ulong height)
    {
        RollupXParentAuthorizer authorizer = Authorizer();
        RollupXAuthorization result = await authorizer.Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        _source.Blocks[height] = _source.Blocks[height] with { Hash = TestItem.KeccakC };
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => authorizer.Recheck(result, CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Aborted), "recheck binds every canonical block used for authorization");
    }

    [Test]
    public async Task Authorize_ReorgDuringDeploymentReads_AbortsBeforeReturningContext()
    {
        _source.BeforeCall = () => _source.Blocks[101] = _source.Blocks[101] with { Hash = TestItem.KeccakC };
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Aborted), "canonical evidence is rechecked after all deployment I/O");
    }

    [Test]
    public async Task Authorize_InputChangedDuringReads_UsesOriginalSelectedRequest()
    {
        Begin begin = _source.Begin();
        L1PostingContext target = _source.Target();
        _source.BeforeCall = () => { begin.AnchorNumber++; target.TargetTimestamp++; target.ParentNumber--; };
        RollupXAuthorization result = await Authorizer().Authorize(begin, target, CancellationToken.None);
        Assert.That((Rlp.Decode<BlockHeader>(result.ParentHeader.ToByteArray())!.Number, result.Target.Timestamp, result.L1ParentNumber),
            Is.EqualTo((5UL, 1030UL, 105UL)), "mutable protobuf messages cannot change authorization after admission");
    }

    [Test]
    public async Task Authorize_HistoryBudgetExceeded_IsUnavailableWithoutFallback()
    {
        _source.ClearBatch(101);
        _source.AddBatch(104, 0, _source.Parent.Hash!);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer(historyBlocks: 1).Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _source.RangeReads), Is.EqualTo((StatusCode.Unavailable, 1)), "a bounded lookup does not turn registry-root equality into provenance");
    }

    [Test]
    public async Task Authorize_NoExactLiveSlot_PreservesSeedFromCatchUp()
    {
        _source.ClearBatch(101);
        _source.Blocks[101] = _source.Blocks[101] with { Timestamp = 1006 };
        _source.AddBatch(104, 0, _source.Parent.Hash!);
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        Assert.That(result.DescendantSeed, Is.EqualTo(_source.Parent.MixHash), "a missing canonical timestamp has no live refresh endpoint");
    }

    [Test]
    public async Task Authorize_CallerCancelled_StopsBeforeSourceReads()
    {
        await Assert.CatchAsync<OperationCanceledException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), new CancellationToken(true)));
        Assert.That(_source.Requests, Is.EqualTo(0), "a cancelled attempt does not consume evidence resources");
    }

    [Test]
    public async Task Authorize_CancelledDuringEvidence_StopsOwnedReads()
    {
        using CancellationTokenSource cancellation = new();
        _source.BeforeCall = cancellation.Cancel;
        await Assert.CatchAsync<OperationCanceledException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), cancellation.Token));
        Assert.That(_source.Calls, Is.EqualTo(0), "caller cancellation is checked inside the configured source before another response");
    }

    [Test]
    public async Task Authorize_OverallDeadlineDuringEvidence_IsUnavailable()
    {
        StreamingTransportTests.ManualTime time = new();
        _source.BeforeCall = () => time.Advance(TimeSpan.FromSeconds(30));
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer(time: time).Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unavailable), "the overall deadline bounds a sequence of individually successful RPC reads");
    }

    [Test]
    public async Task Recheck_ContextFromDifferentDeployment_RefusesBeforeReading()
    {
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        int reads = _source.Requests;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Recheck(result, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _source.Requests), Is.EqualTo((StatusCode.FailedPrecondition, reads)), "the authenticated context belongs to its original deployment");
    }

    [Test]
    public async Task Authorize_TemporaryForkSkipsLiveSlot_AbortsAfterCanonicalChainReturns()
    {
        _source.AddBatch(104, 0, _source.Parent.Hash!);
        _source.TemporaryFork = true;
        Assert.That(_source.Parent.MixHash, Is.Not.EqualTo(_source.Blocks[101].MixHash), "precondition: missing the live slot changes the descendant seed");
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _source.Calls), Is.EqualTo((StatusCode.Aborted, 4)), "search-probe rechecks reject A-to-B-to-A evidence even when later catch-up and deployment reads succeed");
    }

    [Test]
    public async Task Authorize_LiveSlotAcceptedOnDifferentFork_RejectsConflictingSameHeightEvidence()
    {
        _source.TemporaryFork = true;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _source.Calls), Is.EqualTo((StatusCode.Aborted, 0)), "conflicting evidence at the accepted height stops before deployment reads");
    }

    [Test]
    public async Task Authorize_SearchProbesPinned_RechecksTargetLast()
    {
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        Assert.That((result.CanonicalEvidence.Any(static h => h.Number == 102), _source.BlockReads[^1]), Is.EqualTo((true, 105UL)),
            "timestamp-search evidence is retained and the named target is the last canonical recheck");
    }

    [TestCase(false, TestName = "Authorize_WrapperTriggeredRoot_RetainsEarlierDirectBatchWindow")]
    [TestCase(true, TestName = "Authorize_ForeignBatch_DoesNotCloseOurQueuedWindow")]
    public async Task Authorize_RootEmittedAfterPostingTransaction_KeepsEarlierBatchAttribution(bool foreign)
    {
        _source.RootTransactionOffset = foreign ? 2UL : 1UL;
        if (foreign) _source.Batches[(101, 1)] = new PostBatch([], [], [], 0, 0, [TestItem.AddressD], [new RollupProofSystems(2, [0])], [], [], [[]], ulong.MaxValue, false);
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        Assert.That(result.DescendantSeed, Is.EqualTo(_source.Blocks[101].MixHash), "later execution of queued roots belongs to the earlier direct batch, across a foreign batch");
    }

    [Test]
    public async Task Authorize_SkippedAnchor_AttributesOnlyConsumedTerminalSuffix()
    {
        _source.ClearBatch(101);
        _source.AddBatch(101, 0, _source.Genesis.Hash!, _source.Parent.Hash!);
        _source.Skipped[(101, 0)] = [0];
        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None);
        Assert.That(result.DescendantSeed, Is.EqualTo(_source.Blocks[101].MixHash), "a skipped leading update does not discard the actual settled suffix");
    }

    [Test]
    public async Task Authorize_SupersededNoOp_DoesNotInventParentTerminalAcceptance()
    {
        _source.ClearBatch(101);
        BlockHeader endpoint = Evidence.Header(5, 1005, TestItem.KeccakA, [0xEE]);
        _source.Headers[endpoint.Hash!] = endpoint;
        _source.AddBatch(101, 0, _source.Parent.Hash!);
        _source.AddBatch(101, 1, _source.Parent.Hash!, endpoint.Hash!);
        for (ulong index = 0; index < 2; index++)
        {
            PostBatch batch = _source.Batches[(101, index)];
            ExecutionEntry entry = batch.Entries[0];
            entry.RollupUpdates[0] = entry.RollupUpdates[0] with { CurrentRoot = _source.Parent.Hash!.ValueHash256 };
        }
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unavailable), "a richer batch supersedes the anchor-only no-op, so the no-op root is not a terminal endpoint");
    }

    [Test]
    public async Task Authorize_TooManyBatchTransactions_IsUnavailableBeforeFetchingThem()
    {
        for (ulong index = 1; index <= 128; index++) _source.AddBatch(101, index, _source.Parent.Hash!);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _source.TransactionReads), Is.EqualTo((StatusCode.Unavailable, 0)), "the batch-count limit bounds transaction fan-out before any batch fetch");
    }

    [Test]
    public async Task Authorize_PagedHistoryExhaustion_IsBoundedAndUnavailable()
    {
        _source.ClearBatch(101);
        _source.TargetNumber = 5100;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _source.RangeReads), Is.EqualTo((StatusCode.Unavailable, 32)), "4096 heights produce exactly32 bounded128-block range requests");
    }

    [Test]
    public async Task Authorize_OtherGenesisWithSameSeed_RefusesConfiguredGenesisMismatch()
    {
        BlockHeader other = Evidence.Header(0, 1000, TestItem.KeccakA, [0xFE]);
        _source.Headers[other.Hash!] = other;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(other), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition), "only the exact configured genesis has the acceptance exception");
    }

    [TestCase(0, TestName = "Constructor_ZeroHistoryBudget_Refuses")]
    [TestCase(4097, TestName = "Constructor_UnboundedHistoryBudget_Refuses")]
    public void Constructor_InvalidHistoryBudget_Refuses(int blocks) =>
        Assert.That(() => Authorizer(historyBlocks: blocks), Throws.ArgumentException, "history work must remain within its finite operator bound");

    [Test]
    public async Task Authorize_SkipOutsidePostedEntries_IsUnavailable()
    {
        _source.Skipped[(101, 0)] = [1];
        Assert.That(_source.Batches[(101, 0)].Entries, Has.Length.EqualTo(1), "precondition: the skip index has no posted entry");
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unavailable), "skips must be fetched and checked against the exact posted entries");
    }

    [Test]
    public async Task Authorize_TooManyDistinctAcceptanceCandidates_StopsBeforeTheNextFetch()
    {
        _source.ClearBatch(101);
        _source.TargetNumber = 300;
        BlockHeader endpoint = Evidence.Header(10, 1010, TestItem.KeccakA, [0xFA]);
        _source.Headers[endpoint.Hash!] = endpoint;
        for (ulong number = 172; number <= 300; number++)
        {
            _source.Blocks[number] = new CanonicalL1Header(number, Keccak.Compute("L1-" + number), 1000 + (number - 100) * 5, Keccak.Compute("seed-" + number));
            _source.AddBatch(number, 0, _source.Parent.Hash!, endpoint.Hash!);
        }
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _source.RangeReads, _source.TransactionReads), Is.EqualTo((StatusCode.Unavailable, 2, 128)),
            "the global candidate budget applies across pages and different canonical blocks");
    }

    [TestCase(false, TestName = "Authorize_V1LiveParent_ValidatesExactProvenance")]
    [TestCase(true, TestName = "Authorize_V1LaterAcceptance_ValidatesCanonicalAlternative")]
    public async Task Authorize_V1Provenance_AuthenticatesAcceptanceAndOrderedEndpoints(bool later)
    {
        if (later) _source.AddBatch(104, 0, _source.Parent.Hash!);
        Nethermind.Eez.Prove.ParentProvenance provenance = new()
        {
            ParentHeaderRlp = ByteString.CopyFrom(Rlp.Encode(_source.Parent).Bytes),
            AcceptedL1Number = later ? 104UL : 101UL,
            LiveSlotEndpointHeadersRlp = { ByteString.CopyFrom(Rlp.Encode(_source.Parent).Bytes) },
        };

        RollupXAuthorization result = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None, provenance);

        Assert.That(result.CanonicalEvidence.Any(header => header.Number == provenance.AcceptedL1Number), Is.True,
            "the explicitly selected canonical acceptance remains part of the final signing recheck");
        Assert.That(result.DescendantSeed, Is.EqualTo(_source.Blocks[101].MixHash), "later acceptance does not replace the original live-slot seed");
    }

    [TestCase("parent", TestName = "Authorize_V1DifferentParentRlp_Rejects")]
    [TestCase("acceptance", TestName = "Authorize_V1UnacceptedParent_Rejects")]
    [TestCase("future", TestName = "Authorize_V1FutureAcceptance_Rejects")]
    [TestCase("missing", TestName = "Authorize_V1MissingLiveEndpoint_Rejects")]
    [TestCase("duplicate", TestName = "Authorize_V1DuplicateLiveEndpoint_Rejects")]
    [TestCase("order", TestName = "Authorize_V1ReorderedLiveEndpoints_Rejects")]
    public async Task Authorize_V1ChangedProvenance_Rejects(string mutation)
    {
        BlockHeader next = Evidence.Header(5, 1005, TestItem.KeccakA, [0xBB]);
        _source.Headers[next.Hash!] = next;
        _source.AddBatch(101, 0, _source.Parent.Hash!, next.Hash!);
        Nethermind.Eez.Prove.ParentProvenance provenance = new()
        {
            ParentHeaderRlp = ByteString.CopyFrom(Rlp.Encode(_source.Parent).Bytes),
            AcceptedL1Number = 101,
            LiveSlotEndpointHeadersRlp = { ByteString.CopyFrom(Rlp.Encode(next).Bytes) },
        };
        // One native batch contributes its final endpoint. A second accepted
        // batch makes endpoint ordering independently observable.
        _source.AddBatch(101, 1, _source.Parent.Hash!);
        provenance.LiveSlotEndpointHeadersRlp.Add(provenance.ParentHeaderRlp);
        RollupXAuthorization unchanged = await Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None, provenance);
        Assert.That(unchanged.ParentHeader, Is.EqualTo(provenance.ParentHeaderRlp), "precondition: complete unmodified two-endpoint provenance is accepted");
        switch (mutation)
        {
            case "parent": provenance.ParentHeaderRlp = ByteString.CopyFrom(Rlp.Encode(next).Bytes); break;
            case "acceptance": provenance.AcceptedL1Number = 102; break;
            case "future": provenance.AcceptedL1Number = 106; break;
            case "missing": provenance.LiveSlotEndpointHeadersRlp.RemoveAt(0); break;
            case "duplicate": provenance.LiveSlotEndpointHeadersRlp.Add(provenance.ParentHeaderRlp); break;
            case "order":
                ByteString first = provenance.LiveSlotEndpointHeadersRlp[0];
                provenance.LiveSlotEndpointHeadersRlp[0] = provenance.LiveSlotEndpointHeadersRlp[1];
                provenance.LiveSlotEndpointHeadersRlp[1] = first;
                break;
        }

        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Authorizer().Authorize(_source.Begin(), _source.Target(), CancellationToken.None, provenance)))!;

        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition), "untrusted provenance never substitutes for canonical acceptance and its complete endpoint set");
    }

    [Test]
    public async Task Authorize_V1ExplicitCatchUpAcceptance_UsesExactBlockOutsideSearchHorizon()
    {
        _source.ClearBatch(101);
        _source.AddBatch(102, 0, _source.Parent.Hash!);
        Nethermind.Eez.Prove.ParentProvenance provenance = new()
        {
            ParentHeaderRlp = ByteString.CopyFrom(Rlp.Encode(_source.Parent).Bytes),
            AcceptedL1Number = 102,
        };

        RollupXAuthorization result = await Authorizer(historyBlocks: 1).Authorize(_source.Begin(), _source.Target(), CancellationToken.None, provenance);

        Assert.That(result.CanonicalEvidence.Any(static header => header.Number == 102), Is.True,
            "the exact supplied acceptance is authenticated directly instead of depending on a history scan");
        Assert.That(_source.RangeReads, Is.Zero, "explicit provenance avoids an unrelated bounded acceptance search");
    }

    private RollupXParentAuthorizer Authorizer(int historyBlocks = 4096, TimeProvider? time = null) =>
        new(new SettlementRpcReader(_l1, TimeSpan.FromSeconds(5)), new SettlementRpcReader(_l2, TimeSpan.FromSeconds(5)),
            TestItem.AddressA, TestItem.AddressB, _source.Context, TestItem.AddressC, _source.Schedule, _source.Genesis.Hash!, historyBlocks, time: time);

    private sealed class Handler(Evidence evidence, bool l2) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            JsonElement json = document.RootElement;
            object? result = evidence.Read(json.GetProperty("method").GetString()!, json.GetProperty("params"), l2, cancellation);
            return new HttpResponseMessage(evidence.Mutation == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = evidence.Mutation == "missing" ? null : result })) };
        }
    }

    private sealed class Evidence
    {
        internal readonly EezSettlementContext Context = new(1, 699069, TestItem.AddressD, TestItem.KeccakD.ValueHash256, 1);
        internal readonly Dictionary<ulong, CanonicalL1Header> Blocks = [];
        internal readonly Dictionary<Hash256, BlockHeader> Headers = [];
        internal readonly Dictionary<(ulong Number, ulong Index), PostBatch> Batches = [];
        internal readonly List<ulong> BlockReads = [];
        internal readonly List<string> HeaderReads = [];
        internal readonly Dictionary<(ulong Number, ulong Index), HashSet<ulong>> Skipped = [];
        internal readonly BlockHeader Genesis;
        internal BlockHeader Parent;
        internal Hash256 RegistryRoot;
        internal RollupXSchedule Schedule;
        internal CanonicalL1Header Finalized;
        internal string Mutation = "";
        internal Action? BeforeCall;
        internal int Requests, Calls, LogReads, RangeReads, TransactionReads;
        internal bool TemporaryFork;
        internal ulong RootTransactionOffset;
        internal ulong TargetNumber = 105;
        private static readonly Hash256 RootTopic = Keccak.Compute("L2ExecutionPerformed(uint64,bytes32,uint256)");
        private static readonly Hash256 BatchTopic = Keccak.Compute("BatchPosted(bytes32,uint64[])");

        internal Evidence()
        {
            for (ulong number = 100; number <= 105; number++)
                Blocks[number] = new CanonicalL1Header(number, Keccak.Compute("L1-" + number), 1000 + (number - 100) * 5,
                    number == 100 ? TestItem.KeccakA : Keccak.Compute("seed-" + number));
            CanonicalL1Header reference = Blocks[100];
            Finalized = reference;
            Schedule = new RollupXSchedule(1, 5, new RollupXL1Reference(10200, 100, reference.Hash, 1000, reference.MixHash), 1000);
            Genesis = Header(0, 1000, TestItem.KeccakA, [0x00]);
            Parent = Header(5, 1005, TestItem.KeccakA, [0xAA]);
            Headers[Genesis.Hash!] = Genesis;
            Headers[Parent.Hash!] = Parent;
            RegistryRoot = Parent.Hash!;
            AddBatch(101, 0, Parent.Hash!);
        }

        internal Begin Begin(BlockHeader? parent = null) => new()
        {
            RollupId = 1,
            AnchorNumber = (parent ?? Parent).Number,
            AnchorHash = ByteString.CopyFrom((parent ?? Parent).Hash!.Bytes),
            AnchorStateRoot = ByteString.CopyFrom((parent ?? Parent).StateRoot!.Bytes)
        };
        internal L1PostingContext Target() => new() { ChainId = 10200, TargetTimestamp = BlockAt(TargetNumber).Timestamp + 5, ParentNumber = TargetNumber, ParentHash = ByteString.CopyFrom(BlockAt(TargetNumber).Hash.Bytes) };
        internal void ClearBatch(ulong number)
        {
            foreach ((ulong Number, ulong Index) key in Batches.Keys.Where(k => k.Number == number).ToArray()) Batches.Remove(key);
        }
        internal void ReplaceParent(BlockHeader parent)
        {
            parent.Hash = Keccak.Compute(Rlp.Encode(parent).Bytes);
            Parent = parent;
            Headers[parent.Hash] = parent;
            ClearBatch(101);
            AddBatch(101, 0, parent.Hash);
        }
        internal void AddBatch(ulong number, ulong index, params Hash256[] roots)
        {
            ValueHash256 current = TestItem.KeccakB;
            ExecutionEntry[] entries = new ExecutionEntry[roots.Length];
            for (int i = 0; i < roots.Length; i++)
            {
                entries[i] = new ExecutionEntry([new RollupUpdate(1, current, roots[i], 0)], default, [], [], default, 1, true, []);
                current = roots[i];
            }
            Batches[(number, index)] = new PostBatch([], entries, [], (ulong)roots.Length, 0, [TestItem.AddressD], [new RollupProofSystems(1, [0])], [], [], [[]], ulong.MaxValue, false);
        }

        internal object? Read(string method, JsonElement parameters, bool l2, CancellationToken cancellation)
        {
            Requests++;
            switch (method)
            {
                case "eth_chainId": return Mutation == (l2 ? "l2chain" : "l1chain") ? "0x2" : l2 ? Hex(Context.ChainId) : Hex(10200);
                case "debug_getRawHeader":
                    BlockHeader header = Headers[new Hash256(parameters[0].GetString()!)];
                    byte[] raw = Rlp.Encode(Mutation == "rawhash" ? Genesis : header).Bytes;
                    return (Mutation == "rawtrailing" ? [.. raw, 0] : raw).ToHexString(true);
                case "eth_getBlockByNumber":
                    string selector = parameters[0].GetString()!;
                    HeaderReads.Add(selector);
                    if (selector == "finalized") return FinalizedBlock();
                    ulong number = Convert.ToUInt64(selector, 16);
                    BlockReads.Add(number);
                    CanonicalL1Header block = BlockAt(number);
                    if (TemporaryFork && number is 101 or 102 && BlockReads.Count(n => n == number) == 1)
                        block = block with { Hash = Keccak.Compute("temporary-" + number), Timestamp = 1000 + (number - 100) * 5 + 5 };
                    return new
                    {
                        number = Hex(Mutation == "height" ? number + 1 : number),
                        hash = Mutation == "reference" && number == 100 ? TestItem.KeccakC.ToString() : block.Hash.ToString(),
                        timestamp = Hex(block.Timestamp),
                        mixHash = Mutation == "referenceSeed" && number == 100 ? TestItem.KeccakC.ToString() : block.MixHash.ToString()
                    };
                case "eth_call":
                    BeforeCall?.Invoke();
                    cancellation.ThrowIfCancellationRequested();
                    Calls++;
                    Assert.That((parameters[1].GetProperty("blockHash").GetString(), parameters[1].GetProperty("requireCanonical").GetBoolean()),
                        Is.EqualTo((BlockAt(TargetNumber).Hash.ToString(), true)), "all deployment state uses the exact canonical target parent");
                    return Call(parameters[0]).ToHexString(true);
                case "eth_getLogs": return Logs(parameters[0]);
                case "eth_getTransactionByBlockHashAndIndex":
                    TransactionReads++;
                    CanonicalL1Header containing = Blocks.Values.Single(b => b.Hash.ToString() == parameters[0].GetString());
                    ulong index = Convert.ToUInt64(parameters[1].GetString(), 16);
                    return new
                    {
                        hash = (Mutation == "txhash" ? TestItem.KeccakC : TxHash(containing.Number, index)).ToString(),
                        blockHash = (Mutation == "txblock" ? TestItem.KeccakC : containing.Hash).ToString(),
                        blockNumber = Hex(containing.Number),
                        transactionIndex = Hex(Mutation == "txindex" ? index + 1 : index),
                        to = (Mutation == "wrapper" ? TestItem.AddressC : TestItem.AddressA).ToString(),
                        input = (Mutation == "badabi" ? new byte[] { 0x00 } : EezCalldata.EncodePostAndVerifyBatch(Batches[(containing.Number, index)])).ToHexString(true)
                    };
                default: throw new InvalidOperationException(method);
            }
        }

        private object? FinalizedBlock() => Mutation == "finalizedmissing" ? null : new
        {
            number = Mutation == "finalizednumberEncoding" ? "100" : Hex(Mutation == "finalizedheight" ? 99 : Finalized.Number),
            hash = (Mutation == "finalizedhash" ? TestItem.KeccakC : Mutation == "finalizedzeroHash" ? Keccak.Zero : Finalized.Hash).ToString(),
            timestamp = Hex(Mutation == "finalizedtimestamp" ? 999 : Finalized.Timestamp),
            mixHash = Finalized.MixHash.ToString()
        };

        private object Logs(JsonElement filter)
        {
            LogReads++;
            Hash256 topic = new(filter.GetProperty("topics")[0].GetString()!);
            Assert.That(filter.GetProperty("address").GetString(), Is.EqualTo(TestItem.AddressA.ToString()), "logs come from the configured registry only");
            if (topic == RootTopic)
                Assert.That((filter.GetProperty("topics").GetArrayLength(), filter.GetProperty("topics")[1].GetString()),
                    Is.EqualTo((2, Word(1).ToHexString(true))), "root logs are filtered by the configured rollup id");
            bool byHash = filter.TryGetProperty("blockHash", out JsonElement hash);
            ulong from = byHash ? Blocks.Values.Single(b => b.Hash.ToString() == hash.GetString()).Number : Convert.ToUInt64(filter.GetProperty("fromBlock").GetString(), 16);
            ulong to = byHash ? from : Convert.ToUInt64(filter.GetProperty("toBlock").GetString(), 16);
            if (!byHash)
            {
                RangeReads++;
                Assert.That(topic, Is.EqualTo(RootTopic), "only discovery roots use a height range");
                Assert.That(to - from + 1, Is.LessThanOrEqualTo(128UL), "each history query stays within its page-width bound");
                Assert.That((from >= 100, to <= TargetNumber), Is.EqualTo((true, true)), "discovery stays within the configured reference and target");
            }
            else
                Assert.That(filter.TryGetProperty("fromBlock", out _) || filter.TryGetProperty("toBlock", out _), Is.False,
                    "same-block attribution uses an exact canonical hash without a conflicting height range");
            List<object> logs = [];
            foreach (((ulong Number, ulong Index) key, PostBatch batch) in Batches.OrderBy(static p => p.Key.Number).ThenBy(static p => p.Key.Index))
            {
                if (key.Number < from || key.Number > to) continue;
                if (topic == RootTopic)
                {
                    for (int i = 0; i < batch.Entries.Length; i++)
                    {
                        if (batch.Entries[i].RollupUpdates[0].RollupId != 1 ||
                            (Skipped.TryGetValue(key, out HashSet<ulong>? skipped) && skipped.Contains((ulong)i))) continue;
                        byte[] data = new byte[64];
                        batch.Entries[i].RollupUpdates[0].NewRoot.Bytes.CopyTo(data);
                        object log = Log(key.Number, key.Index + RootTransactionOffset, (key.Index + RootTransactionOffset) * 100 + (ulong)i, topic, data,
                            Mutation == "logtopic" ? TestItem.KeccakC.ToString() : Word(1).ToHexString(true));
                        logs.Add(log);
                        if (Mutation == "duplicate") logs.Add(log);
                    }
                }
                else if (topic == BatchTopic)
                {
                    byte[] data = new byte[128];
                    data[63] = 64; data[95] = 1; data[127] = Mutation == "batchdata" ? (byte)2 : (byte)batch.RollupIdsWithProofSystems[0].RollupId;
                    logs.Add(Log(key.Number, key.Index, key.Index * 100 + 50, topic, data));
                }
                else if (Skipped.TryGetValue(key, out HashSet<ulong>? skipped))
                {
                    foreach (ulong index in skipped)
                    {
                        byte[] data = new byte[64];
                        data[31] = 32;
                        logs.Add(Log(key.Number, key.Index, key.Index * 100 + 60 + index, topic, data, Word(index).ToHexString(true)));
                    }
                }
            }
            return logs;
        }

        private object Log(ulong number, ulong transaction, ulong index, Hash256 topic, byte[] data, string? topic1 = null) => new
        {
            address = (Mutation == "logaddress" ? TestItem.AddressC : TestItem.AddressA).ToString(),
            removed = Mutation == "removed",
            blockNumber = Hex(number),
            blockHash = (Mutation == "logfork" ? TestItem.KeccakC : Blocks[number].Hash).ToString(),
            transactionIndex = Hex(transaction),
            transactionHash = TxHash(number, transaction).ToString(),
            logIndex = Hex(index),
            topics = topic1 is null ? new[] { topic.ToString() } : [topic.ToString(), topic1],
            data = (Mutation == "rootdata" && topic == RootTopic ? data[..32] : data).ToHexString(true),
        };

        private byte[] Call(JsonElement request)
        {
            byte[] selector = Convert.FromHexString(request.GetProperty("data").GetString()!.AsSpan(2));
            if (selector.AsSpan(0, 4).SequenceEqual(Keccak.Compute("rollups(uint64)").Bytes[..4]))
            {
                Assert.That((request.GetProperty("to").GetString(), selector.Length, selector[^1]),
                    Is.EqualTo((TestItem.AddressA.ToString(), 36, (byte)1)), "registration binds the configured registry and rollup id");
                byte[] tuple = new byte[96];
                TestItem.AddressB.Bytes.CopyTo(tuple.AsSpan(12));
                RegistryRoot.Bytes.CopyTo(tuple.AsSpan(32));
                if (Mutation == "manager") tuple[12] ^= 1;
                if (Mutation == "padding") tuple[0] = 1;
                if (Mutation == "zeroRoot") tuple.AsSpan(32, 32).Clear();
                return tuple;
            }
            if (selector.AsSpan(0, 4).SequenceEqual(Keccak.Compute("verificationKey(address)").Bytes[..4]))
            {
                Assert.That((request.GetProperty("to").GetString(), selector.AsSpan(16).ToArray()),
                    Is.EqualTo((TestItem.AddressB.ToString(), Context.ProofSystem.Bytes.ToArray())), "verification key lookup binds the manager and proof system");
                byte[] key = Context.VerificationKey.ToByteArray();
                if (Mutation == "vkey") key[0] ^= 1;
                return key;
            }
            if (selector.AsSpan(0, 4).SequenceEqual(Keccak.Compute("signer()").Bytes[..4]))
            {
                Assert.That((request.GetProperty("to").GetString(), selector.Length), Is.EqualTo((Context.ProofSystem.ToString(), 4)),
                    "signer lookup binds the configured proof system");
                byte[] word = new byte[32];
                TestItem.AddressC.Bytes.CopyTo(word.AsSpan(12));
                if (Mutation == "signer") word[12] ^= 1;
                return word;
            }
            Assert.That((request.GetProperty("to").GetString(), selector), Is.EqualTo((TestItem.AddressB.ToString(), Keccak.Compute("threshold()").Bytes[..4].ToArray())),
                "threshold lookup binds the registered manager");
            byte[] threshold = new byte[32];
            threshold[^1] = Mutation == "threshold" ? (byte)2 : (byte)1;
            return threshold;
        }

        internal static BlockHeader Header(ulong number, ulong timestamp, Hash256 seed, byte[] extra)
        {
            BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithTimestamp(timestamp).WithGasLimit(EezChainRules.RollupXBlockGasLimit)
                .WithExtraData(extra).WithStateRoot(TestItem.KeccakA).TestObject;
            header.Difficulty = 0;
            header.Nonce = 0;
            header.MixHash = seed;
            header.WithdrawalsRoot = Keccak.EmptyTreeHash;
            header.ParentBeaconBlockRoot = Keccak.Zero;
            header.RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash;
            header.BlobGasUsed = 0;
            header.ExcessBlobGas = 0;
            header.Hash = Keccak.Compute(Rlp.Encode(header).Bytes);
            return header;
        }

        private CanonicalL1Header BlockAt(ulong number) => Blocks.TryGetValue(number, out CanonicalL1Header? known) ? known :
            new CanonicalL1Header(number, Keccak.Compute("L1-" + number), 1000 + (number - 100) * 5, Keccak.Compute("seed-" + number));

        private static byte[] Word(ulong number)
        {
            byte[] bytes = new byte[32];
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), number);
            return bytes;
        }
        private static string Hex(ulong number) => "0x" + number.ToString("x", CultureInfo.InvariantCulture);
        private static Hash256 TxHash(ulong number, ulong index) => Keccak.Compute($"tx-{number}-{index}");
    }
}
