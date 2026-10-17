// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using L1PostingContext = Nethermind.Eez.Prove.V2.L1PostingContext;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using SettlementBatch = Nethermind.Eez.Execution.Settlement.PostBatch;
using StreamBlock = Nethermind.Eez.Prove.V2.Block;

namespace Nethermind.Eez.Test;

[TestFixture(StatelessFixtures.Window84, 79, 84)]
[TestFixture("captured-devnet-window-468", 463, 468)]
public class StreamingFinalizerTests(string fixture, int from, int to)
{
    private readonly string _fixture = fixture;
    private List<ProveChunk> _chunks = null!;
    private EezSettlementContext _context = null!;
    private BlockHeader _parent = null!;
    private Begin _predecessor = null!;
    private EvidenceHandler _l1 = null!;
    private EvidenceHandler _l2 = null!;
    private HttpClient _l1Client = null!;
    private HttpClient _l2Client = null!;
    private Rollup0ParentAuthorizer _authorizer = null!;
    private StreamingFinalizer _finalizer = null!;
    private StreamingBlockExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _chunks = AttesterTests.Window(_fixture, from, to);
        SettlementBatch recorded = EezCalldata.DecodePostAndVerifyBatch(_chunks[0].Header.PostBatch.AbiCalldata.Span);
        Assert.That(recorded.Proofs, Has.Length.EqualTo(1), "the recorded settlement has one mined proof");
        Assert.That(recorded.Proofs[0], Has.Length.EqualTo(EezAttestationSigner.SignatureLength), "the recorded fixture includes the L1 proof");
        _chunks[0].Header.PostBatch.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(recorded with { Proofs = [[]] }));
        _parent = Rlp.Decode<BlockHeader>(_chunks[1].Block.Witness.Headers.Single().ToByteArray())!;
        ISpecProvider specs = StatelessFixtures.ReadSpecProvider(_fixture, "chain-config.json");
        JsonElement oracle = StatelessFixtures.ReadJson(_fixture, "oracle.json");
        _context = new EezSettlementContext(1, specs.ChainId, new Address(oracle.GetProperty("proof_system").GetString()!),
            new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!), 2);
        _predecessor = new Begin
        {
            RollupId = 1,
            AnchorNumber = _parent.Number,
            AnchorHash = ByteString.CopyFrom(_parent.Hash!.Bytes),
            AnchorStateRoot = ByteString.CopyFrom(_parent.StateRoot!.Bytes)
        };
        _l1 = new EvidenceHandler(_context, _parent, false);
        _l2 = new EvidenceHandler(_context, _parent, true);
        _l1Client = new HttpClient(_l1) { BaseAddress = new Uri("http://l1.invalid") };
        _l2Client = new HttpClient(_l2) { BaseAddress = new Uri("http://l2.invalid") };
        _authorizer = new Rollup0ParentAuthorizer(new SettlementRpcReader(_l1Client, TimeSpan.FromSeconds(5)),
            new SettlementRpcReader(_l2Client, TimeSpan.FromSeconds(5)), 31337, TestItem.AddressA, TestItem.AddressB, _context, AttesterServer.AttesterKey.Address);
        EezAttestationSigner signer = new(AttesterServer.AttesterKey);
        _finalizer = new StreamingFinalizer(new AttestationPipeline(specs, _context, signer, LimboLogs.Instance), signer, _authorizer);
        _executor = new StreamingBlockExecutor(specs, EezChainRules.Rollup0, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() { _l1Client.Dispose(); _l2Client.Dispose(); }

    [Test]
    public async Task Run_RecordedWindowWithOwnedEvidence_ReturnsIndependentReferenceProofAndEof()
    {
        using StreamingSessionRegistry registry = new(new StreamingRegistryLimits(4, 4, 128 * 1024 * 1024, TimeSpan.FromMinutes(10)),
            new StreamingSessionLimits(8, 16 * 1024 * 1024, 100_000), TimeProvider.System);
        BackendAdmission admission = new(NullLogger<BackendAdmission>.Instance);
        RecordingLogger logger = new();
        StreamingTransport transport = new(registry, admission, _executor,
            new StreamingTransportLimits(2, 16 * 1024 * 1024, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            1, logger, finalize: _finalizer.Finalize);
        StreamingTransportTests.Reader input = new();
        StreamingTransportTests.Writer output = new();
        Dictionary<ulong, TaskCompletionSource> acknowledged = [];
        for (ulong number = (ulong)from - 1; number <= (ulong)to; number++)
            acknowledged.Add(number, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        output.OnWrite = frame =>
        {
            if (frame.Ready is not null) acknowledged[frame.Ready.ValidatedThrough].TrySetResult();
            if (frame.Validated is not null) acknowledged[frame.Validated.Number].TrySetResult();
            if (frame.Rejected is not null) throw new InvalidOperationException(frame.Rejected.Message);
            return Task.CompletedTask;
        };
        input.Add(() => new ClientFrame { RequestId = 1, Begin = _predecessor });
        for (int i = 1; i < _chunks.Count; i++)
        {
            BlockWitness block = _chunks[i].Block;
            input.AddAsync(async () =>
            {
                await acknowledged[block.Number - 1].Task;
                return Bound(output, block.Number, new StreamBlock { Data = block });
            });
        }
        input.AddAsync(async () =>
        {
            await acknowledged[(ulong)to].Task;
            return Bound(output, 99, Finalization());
        });
        await transport.Run(input, output, "composer", CancellationToken.None);
        await admission.WaitUntilIdle();
        JsonElement oracle = StatelessFixtures.ReadJson(_fixture, "oracle.json");
        JsonElement evidence = JsonSerializer.Deserialize<JsonElement>(logger.Messages.Single(message => message.Contains("eez.proof_signer.window_signed"))).GetProperty("fields");
        Assert.That((evidence.GetProperty("wire").GetString(), evidence.GetProperty("validated_from_block").GetInt32(),
            evidence.GetProperty("validated_to_block").GetInt32(), evidence.GetProperty("recomputed_public_inputs_hash").GetString()),
            Is.EqualTo(("prove.v2", from, to, oracle.GetProperty("public_inputs_hash").GetString())), "wire evidence must name the actual protocol, validated range and independent digest");
        Proof proof = output.Frames[^1].Proof;
        Assert.That(proof, Is.Not.Null, "the recorded window must produce a terminal Proof");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof.PublicInputsHash.ToByteArray().ToHexString(true), Is.EqualTo(oracle.GetProperty("public_inputs_hash").GetString()), "independent recorded public inputs");
            Assert.That(proof.Proof_.ToByteArray().ToHexString(true), Is.EqualTo((oracle.TryGetProperty("mined_proof", out JsonElement mined) ? mined : oracle.GetProperty("expected_test_signature")).GetString()), "independent recorded signature");
            Assert.That((output.Frames.Count, output.Frames[^1].RequestId, _l1.SnapshotReads, _l1.Calls), Is.EqualTo((8, 99UL, 3, 4)),
                "every Finalize reads registration and rechecks canonical authorization after execution");
        }
    }

    [TestCase("number", TestName = "Authorize_WrongParentNumber_Rejects")]
    [TestCase("hash", TestName = "Authorize_WrongParentHash_Rejects")]
    [TestCase("root", TestName = "Authorize_WrongParentRoot_Rejects")]
    [TestCase("rollup", TestName = "Authorize_WrongRollup_Rejects")]
    [TestCase("l1chain", TestName = "Authorize_WrongL1Chain_Rejects")]
    [TestCase("l2chain", TestName = "Authorize_WrongL2Chain_Rejects")]
    [TestCase("manager", TestName = "Authorize_WrongManager_Rejects")]
    [TestCase("accepted", TestName = "Authorize_UnacceptedParent_Rejects")]
    [TestCase("vkey", TestName = "Authorize_WrongVerificationKey_Rejects")]
    [TestCase("signer", TestName = "Authorize_WrongSigner_Rejects")]
    [TestCase("threshold", TestName = "Authorize_UnmetProofThreshold_Rejects")]
    [TestCase("padding", TestName = "Authorize_NoncanonicalManagerWord_Rejects")]
    [TestCase("trailing", TestName = "Authorize_TrailingParentRlp_Rejects")]
    public async Task Authorize_InvalidEvidence_RejectsWithoutActionableDetails(string mutation)
    {
        switch (mutation)
        {
            case "number": _predecessor.AnchorNumber++; break;
            case "hash": _predecessor.AnchorHash = ByteString.CopyFrom(TestItem.KeccakA.Bytes); _l2.ExpectedParentHash = TestItem.KeccakA; break;
            case "root": _predecessor.AnchorStateRoot = ByteString.CopyFrom(TestItem.KeccakA.Bytes); break;
            case "rollup": _predecessor.RollupId++; break;
            case "l2chain": _l2.Mutation = mutation; break;
            case "trailing": _l2.Mutation = mutation; break;
            default: _l1.Mutation = mutation; break;
        }
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => _authorizer.Authorize(_predecessor, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, exception.Trailers.Count), Is.EqualTo((StatusCode.FailedPrecondition, 0)), "authorization failure cannot direct transaction eviction");
    }

    [TestCase("null", TestName = "Authorize_MissingEvidence_IsUnavailable")]
    [TestCase("http", TestName = "Authorize_SourceHttpError_IsUnavailable")]
    [TestCase("error", TestName = "Authorize_SourceRpcError_IsUnavailable")]
    [TestCase("malformed", TestName = "Authorize_MalformedSnapshot_IsUnavailable")]
    public async Task Authorize_UnavailableEvidence_FailsWithoutProof(string mutation)
    {
        _l1.Mutation = mutation;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => _authorizer.Authorize(_predecessor, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, exception.Trailers.Count), Is.EqualTo((StatusCode.Unavailable, 0)), "missing evidence must not be treated as authorization");
    }

    [TestCase(2, TestName = "Finalize_SnapshotReplacedBeforeExecution_AbortsAndPreservesProgress")]
    [TestCase(3, TestName = "Finalize_SnapshotReplacedBeforeSigning_AbortsAndPreservesProgress")]
    public async Task Finalize_SnapshotReplaced_AbortsAndPreservesValidatedProgress(int reorgRead)
    {
        StreamingSession session = ExecutedSession();
        _l1.ReorgOnSnapshotRead = reorgRead;
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, Finalization());
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => _finalizer.Finalize(operation, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, session.Ready(session.Binding, 100).Ready.ValidatedThrough), Is.EqualTo((StatusCode.Aborted, (ulong)to)),
            "reorg rejection keeps validated execution available for a new authorization attempt");
        operation.Dispose();
        _l1.ReorgOnSnapshotRead = int.MaxValue;
        using SessionOperation retry = session.AdmitFinalize(session.Binding, 101, Finalization());
        Assert.That((await _finalizer.Finalize(retry, CancellationToken.None)).PublicInputsHash.Length, Is.EqualTo(32), "the same valid prefix can be signed after evidence recovers");
    }

    [Test]
    public async Task Recheck_HeadAdvancedButNamedSnapshotCanonical_AllowsAuthorization()
    {
        L1AuthorizationSnapshot snapshot = await _authorizer.Authorize(_predecessor, CancellationToken.None);
        _l1.HeadAdvanced = true;
        await _authorizer.Recheck(snapshot, CancellationToken.None);
        Assert.That(_l1.SnapshotReads, Is.EqualTo(3), "recheck uses the named height and does not require the parent to remain tip");
    }

    [TestCase("blobs", StatusCode.Unimplemented)]
    [TestCase("context", StatusCode.Unimplemented)]
    [TestCase("block", StatusCode.FailedPrecondition)]
    [TestCase("sender", StatusCode.FailedPrecondition)]
    [TestCase("proofsystem", StatusCode.FailedPrecondition)]
    [TestCase("rollup", StatusCode.FailedPrecondition)]
    [TestCase("destination", StatusCode.FailedPrecondition)]
    [TestCase("static", StatusCode.Unimplemented)]
    [TestCase("missingproof", StatusCode.InvalidArgument)]
    [TestCase("extraproof", StatusCode.InvalidArgument)]
    public async Task Finalize_InvalidSettlement_FailsBeforeEvidenceAndAllowsValidRetry(string mutation, StatusCode code)
    {
        StreamingSession session = ExecutedSession();
        Finalize finalization = Finalization();
        if (mutation == "blobs") finalization.Settlement.Blobs.Add(ByteString.CopyFrom(new byte[32]));
        else if (mutation == "context") finalization.Settlement.PostingContext = new L1PostingContext();
        else
        {
            SettlementBatch batch = EezCalldata.DecodePostAndVerifyBatch(finalization.Settlement.AbiCalldata.Span);
            batch = mutation switch
            {
                "block" => batch with { BlockNumber = 1 },
                "sender" => batch with { BindMsgSenderInPublicInput = true },
                "proofsystem" => batch with { ProofSystems = [TestItem.AddressC] },
                "rollup" => batch with { RollupIdsWithProofSystems = [new RollupProofSystems(2, [0])] },
                "missingproof" => batch with { Proofs = [] },
                "extraproof" => batch with { Proofs = [[], []] },
                "static" => batch with { ImmediateStaticEntryCount = 1 },
                _ => batch,
            };
            if (mutation == "destination") batch.Entries[0] = batch.Entries[0] with { DestinationRollupId = 2 };
            finalization.Settlement.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch));
        }
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, finalization);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => _finalizer.Finalize(operation, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _l1.Requests, _l2.Requests, session.Ready(session.Binding, 100).Ready.ValidatedThrough),
            Is.EqualTo((code, 0, 0, (ulong)to)), "invalid settlement cannot reach evidence or consume the validated prefix");
        operation.Dispose();
        using SessionOperation retry = session.AdmitFinalize(session.Binding, 101, Finalization());
        JsonElement oracle = StatelessFixtures.ReadJson(_fixture, "oracle.json");
        Assert.That((await _finalizer.Finalize(retry, CancellationToken.None)).PublicInputsHash.ToByteArray().ToHexString(true),
            Is.EqualTo(oracle.GetProperty("public_inputs_hash").GetString()), "the unchanged prefix still produces the independent valid digest");
    }

    [TestCase(0, TestName = "Finalize_ConditionalNoProofSlots_ReturnsRecordedProof")]
    [TestCase(1, TestName = "Finalize_ConditionalEmptyProofSlot_ReturnsRecordedProof")]
    [TestCase(2, TestName = "Finalize_ConditionalInheritedProof_ReturnsFreshRecordedProof")]
    [TestCase(3, TestName = "Finalize_ConditionalInheritedProofCount_ReturnsFreshRecordedProof")]
    public async Task Finalize_ConditionalProofEncoding_ReturnsIndependentOracle(int encoding)
    {
        StreamingSession session = ExecutedSession();
        Finalize finalization = Finalization();
        SettlementBatch batch = EezCalldata.DecodePostAndVerifyBatch(finalization.Settlement.AbiCalldata.Span);
        finalization.Settlement.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch with { Proofs = encoding switch { 0 => [], 1 => [[]], 2 => [[1, 2, 3]], _ => [[1], [2]] } }));
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, finalization);
        JsonElement oracle = StatelessFixtures.ReadJson(_fixture, "oracle.json");
        EezAttestationSigner signer = new(AttesterServer.AttesterKey);
        ISpecProvider specs = StatelessFixtures.ReadSpecProvider(_fixture, "chain-config.json");
        Rollup0ConditionalFinalizer finalizer = new(new AttestationPipeline(specs, _context, signer, LimboLogs.Instance), signer);
        Proof proof = await finalizer.Finalize(operation, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof.PublicInputsHash.ToByteArray().ToHexString(true), Is.EqualTo(oracle.GetProperty("public_inputs_hash").GetString()), "proof slots do not alter validated public inputs");
            Assert.That(proof.Proof_.ToByteArray().ToHexString(true), Is.EqualTo((oracle.TryGetProperty("mined_proof", out JsonElement mined) ? mined : oracle.GetProperty("expected_test_signature")).GetString()), "the original independent signature remains unchanged");
            Assert.That((_l1.Requests == 0, _l2.Requests == 0), Is.EqualTo((true, true)), "only the explicit conditional profile omits L1/L2 acceptance reads");
        }
    }

    [TestCase("parent", StatusCode.FailedPrecondition, TestName = "Finalize_ConditionalFalseParent_Rejects")]
    [TestCase("terminal", StatusCode.FailedPrecondition, TestName = "Finalize_ConditionalFalseTerminal_Rejects")]
    [TestCase("proofsystem", StatusCode.FailedPrecondition, TestName = "Finalize_ConditionalWrongProofSystem_Rejects")]
    [TestCase("blobs", StatusCode.Unimplemented, TestName = "Finalize_ConditionalBlobInput_Rejects")]
    [TestCase("cancelled", StatusCode.Aborted, TestName = "Finalize_ConditionalCancelledSession_Rejects")]
    public async Task Finalize_ConditionalInvalidClaim_ReturnsNoProof(string mutation, StatusCode code)
    {
        StreamingSession session = ExecutedSession();
        Finalize finalization = Finalization();
        SettlementBatch batch = EezCalldata.DecodePostAndVerifyBatch(finalization.Settlement.AbiCalldata.Span);
        if (mutation == "proofsystem") batch = batch with { ProofSystems = [TestItem.AddressA] };
        if (mutation is "parent" or "terminal")
        {
            ExecutionEntry anchor = batch.Entries[0];
            RollupUpdate update = anchor.RollupUpdates[0];
            anchor.RollupUpdates[0] = mutation == "parent" ? update with { CurrentRoot = TestItem.KeccakA.ValueHash256 } : update with { NewRoot = TestItem.KeccakA.ValueHash256 };
        }
        finalization.Settlement.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch));
        if (mutation == "blobs") finalization.Settlement.Blobs.Add(ByteString.CopyFrom(new byte[32]));
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, finalization);
        if (mutation == "cancelled") session.Cancel(session.Binding, 100);
        ISpecProvider specs = StatelessFixtures.ReadSpecProvider(_fixture, "chain-config.json");
        EezAttestationSigner signer = new(AttesterServer.AttesterKey);
        Rollup0ConditionalFinalizer finalizer = new(new AttestationPipeline(specs, _context, signer, LimboLogs.Instance), signer);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => finalizer.Finalize(operation, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _l1.Requests, _l2.Requests), Is.EqualTo((code, 0, 0)), "conditional admission cannot bypass settlement validation or session-owned signing");
    }

    [TestCase("Resume", TestName = "Finalize_ResumedBeforeSigning_DoesNotReturnProof")]
    [TestCase("Rewind", TestName = "Finalize_RewoundBeforeSigning_DoesNotReturnProof")]
    [TestCase("Cancel", TestName = "Finalize_CancelledBeforeSigning_DoesNotReturnProof")]
    public async Task Finalize_ControlChangeDuringFinalRecheck_RejectsActualSignerCaller(string control)
    {
        StreamingSession session = ExecutedSession();
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, Finalization());
        _l1.BeforeFinalRecheck = () =>
        {
            if (control == "Resume") session.Resume("composer");
            else if (control == "Rewind") session.Rewind(session.Binding, 100, _predecessor.AnchorHash);
            else session.Cancel(session.Binding, 100);
        };
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => _finalizer.Finalize(operation, CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Aborted), "the production finalizer must use session-owned signing after the last I/O boundary");
    }

    [Test]
    public async Task Finalize_NonemptyProofInCalldata_RejectsBeforeEvidence()
    {
        StreamingSession session = ExecutedSession();
        Finalize finalization = Finalization();
        SettlementBatch batch = EezCalldata.DecodePostAndVerifyBatch(finalization.Settlement.AbiCalldata.Span);
        batch.Proofs[0] = [1];
        finalization.Settlement.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch));
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, finalization);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => _finalizer.Finalize(operation, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _l1.Requests, _l2.Requests), Is.EqualTo((StatusCode.InvalidArgument, 0, 0)), "Finalize accepts only proofless PostBatch calldata");
    }

    [Test]
    public async Task Finalize_FalseSettlementRoot_RejectsRealExecutionWithoutProof()
    {
        StreamingSession session = ExecutedSession();
        Finalize finalization = Finalization();
        SettlementBatch batch = EezCalldata.DecodePostAndVerifyBatch(finalization.Settlement.AbiCalldata.Span);
        ExecutionEntry anchor = batch.Entries[0];
        RollupUpdate update = anchor.RollupUpdates[0];
        anchor.RollupUpdates[0] = update with { NewRoot = TestItem.KeccakA.ValueHash256 };
        finalization.Settlement.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch));
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, finalization);
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => _finalizer.Finalize(operation, CancellationToken.None)))!;
        Assert.That((exception.StatusCode, _l1.SnapshotReads, session.Ready(session.Binding, 100).Ready.ValidatedThrough),
            Is.EqualTo((StatusCode.FailedPrecondition, 2, (ulong)to)), "actual settlement verification rejects false roots before signing or final recheck");
    }

    [TestCase("Resume", TestName = "SignCurrent_ResumedSession_RejectsStaleWorker")]
    [TestCase("Rewind", TestName = "SignCurrent_RewoundSession_RejectsStaleWorker")]
    [TestCase("Cancel", TestName = "SignCurrent_CancelledSession_RejectsStaleWorker")]
    [TestCase("Dispose", TestName = "SignCurrent_DisposedOperation_RejectsStaleWorker")]
    public void SignCurrent_ControlChange_RejectsBeforeSigner(string control)
    {
        StreamingSession session = ExecutedSession();
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, Finalization());
        switch (control)
        {
            case "Resume": session.Resume("composer"); break;
            case "Rewind": session.Rewind(session.Binding, 100, _predecessor.AnchorHash); break;
            case "Cancel": session.Cancel(session.Binding, 100); break;
            case "Dispose": operation.Dispose(); break;
        }
        Assert.That(() => operation.SignCurrent(new EezAttestationSigner(AttesterServer.AttesterKey), default, CancellationToken.None),
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.StatusCode)).EqualTo(StatusCode.Aborted), "signing and session ownership are checked in the same critical section");
    }

    [Test]
    public void SignCurrent_CancelledToken_StopsBeforeSigner()
    {
        StreamingSession session = ExecutedSession();
        using SessionOperation operation = session.AdmitFinalize(session.Binding, 99, Finalization());
        Assert.That(() => operation.SignCurrent(new EezAttestationSigner(AttesterServer.AttesterKey), default, new CancellationToken(true)),
            Throws.InstanceOf<OperationCanceledException>(), "cancellation is checked while the signing operation owns the session");
    }

    private StreamingSession ExecutedSession()
    {
        StreamingSession session = new("composer", _predecessor, new StreamingSessionLimits(8, 16 * 1024 * 1024, 100_000));
        foreach (ProveChunk chunk in _chunks.Skip(1))
        {
            using SessionOperation operation = session.AdmitBlock(session.Binding, chunk.Block.Number, chunk.Block);
            session.CommitBlock(operation, _executor.Execute(operation, CancellationToken.None));
        }
        return session;
    }

    private Finalize Finalization() => new()
    {
        FromBlock = (ulong)from,
        ToBlock = (ulong)to,
        TerminalHash = _chunks[^1].Block.Hash,
        Settlement = new SettlementInput { AbiCalldata = _chunks[0].Header.PostBatch.AbiCalldata },
    };

    private static ClientFrame Bound(StreamingTransportTests.Writer output, ulong request, object operation)
    {
        ServerFrame attachment = output.Frames[^1];
        ClientFrame frame = new() { SessionId = attachment.SessionId, Epoch = attachment.Epoch, RequestId = request };
        if (operation is StreamBlock block) frame.Block = block;
        else frame.Finalize = (Finalize)operation;
        return frame;
    }

    internal sealed class EvidenceHandler(EezSettlementContext context, BlockHeader parent, bool l2) : HttpMessageHandler
    {
        internal IReadOnlyDictionary<Hash256, BlockHeader>? Headers { get; init; }
        internal string Mutation { get; set; } = "";
        internal Hash256? ExpectedParentHash { get; set; }
        internal Action? BeforeFinalRecheck { get; set; }
        internal int ReorgOnSnapshotRead { get; set; } = int.MaxValue;
        internal bool HeadAdvanced { get; set; }
        internal int Requests { get; private set; }
        internal int SnapshotReads { get; private set; }
        internal int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests++;
            using JsonDocument json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            string method = json.RootElement.GetProperty("method").GetString()!;
            JsonElement parameters = json.RootElement.GetProperty("params");
            object? result;
            switch (method)
            {
                case "eth_chainId": result = Mutation is "l1chain" or "l2chain" ? "0x2" : l2 ? "0x1892" : "0x7a69"; break;
                case "debug_getRawHeader":
                    BlockHeader selected = parent;
                    if (Headers is not null)
                    {
                        if (!Headers.TryGetValue(new Hash256(parameters[0].GetString()!), out BlockHeader? recorded)) { result = null; break; }
                        selected = recorded;
                    }
                    else Assert.That((l2, parameters[0].GetString()), Is.EqualTo((true, (ExpectedParentHash ?? parent.Hash!).ToString())), "raw parent headers come from the configured L2 source");
                    byte[] rlp = Rlp.Encode(selected).Bytes;
                    result = (Mutation == "trailing" ? [.. rlp, 0] : rlp).ToHexString(true);
                    break;
                case "eth_getBlockByNumber":
                    SnapshotReads++;
                    if (SnapshotReads == 3) BeforeFinalRecheck?.Invoke();
                    Assert.That(parameters[1].GetBoolean(), Is.False, "the source never returns full transactions");
                    result = Mutation == "malformed" ? new { hash = "0x00", number = "0x64" } : new
                    {
                        hash = SnapshotReads >= ReorgOnSnapshotRead ? TestItem.KeccakB.ToString() : TestItem.KeccakA.ToString(),
                        number = HeadAdvanced && parameters[0].GetString() == "latest" ? "0x65" : "0x64",
                    };
                    break;
                case "eth_call": result = Call(parameters).ToHexString(true); break;
                default: throw new InvalidOperationException("unexpected evidence method");
            }
            string body = Mutation == "error" ? """{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"private source details"}}""" :
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = Mutation == "null" ? null : result });
            return new HttpResponseMessage(Mutation == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(body) };
        }

        private byte[] Call(JsonElement parameters)
        {
            Calls++;
            Assert.That((parameters[1].GetProperty("blockHash").GetString(), parameters[1].GetProperty("requireCanonical").GetBoolean()),
                Is.EqualTo((TestItem.KeccakA.ToString(), true)), "all registry and proof-system reads use the same canonical hash");
            byte[] data = Convert.FromHexString(parameters[0].GetProperty("data").GetString()!.AsSpan(2));
            string target = parameters[0].GetProperty("to").GetString()!;
            if (data.AsSpan(0, 4).SequenceEqual(Keccak.Compute("rollups(uint64)").Bytes[..4]))
            {
                Assert.That((target, data.Length, data[^1]), Is.EqualTo((TestItem.AddressA.ToString(), 36, (byte)1)), "registry ABI binds the configured rollup id");
                byte[] tuple = new byte[96];
                TestItem.AddressB.Bytes.CopyTo(tuple.AsSpan(12));
                parent.Hash!.Bytes.CopyTo(tuple.AsSpan(32));
                if (Mutation == "manager") tuple[12] ^= 1;
                if (Mutation == "accepted") tuple[32] ^= 1;
                if (Mutation == "padding") tuple[0] = 1;
                return tuple;
            }
            if (data.AsSpan(0, 4).SequenceEqual(Keccak.Compute("verificationKey(address)").Bytes[..4]))
            {
                Assert.That((target, data.AsSpan(16).ToArray()), Is.EqualTo((TestItem.AddressB.ToString(), context.ProofSystem.Bytes.ToArray())), "verification key reads target the registered manager");
                byte[] key = context.VerificationKey.ToByteArray();
                if (Mutation == "vkey") key[0] ^= 1;
                return key;
            }
            if (data.AsSpan(0, 4).SequenceEqual(Keccak.Compute("signer()").Bytes[..4]))
            {
                Assert.That((target, data.Length), Is.EqualTo((context.ProofSystem.ToString(), 4)), "signer reads target the configured proof system");
                byte[] word = new byte[32];
                AttesterServer.AttesterKey.Address.Bytes.CopyTo(word.AsSpan(12));
                if (Mutation == "signer") word[0] = 1;
                return word;
            }
            Assert.That((target, data), Is.EqualTo((TestItem.AddressB.ToString(), Keccak.Compute("threshold()").Bytes[..4].ToArray())), "the supported single proof must meet the manager threshold");
            byte[] threshold = new byte[32];
            threshold[^1] = Mutation == "threshold" ? (byte)2 : (byte)1;
            return threshold;
        }
    }
    private sealed class RecordingLogger : ILogger<StreamingTransport>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

}
