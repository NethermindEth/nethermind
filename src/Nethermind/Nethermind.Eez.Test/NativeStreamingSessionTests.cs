// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Block = Nethermind.Core.Block;
using ProveBlock = Nethermind.Eez.Prove.V2.Block;

namespace Nethermind.Eez.Test;

[NonParallelizable]
[TestFixture(3)]
[TestFixture(4)]
[TestFixture(6)]
public sealed class NativeStreamingSessionTests(int version)
{
    private string Capture => $"rollupx/native-offline-v{version}";
    private readonly List<ClientFrame> _frames = [];
    private readonly List<BlockWitness> _blocks = [];
    private NativeStreamingProfile _profile = null!;
    private StreamingBlockExecutor _executor = null!;

    [OneTimeSetUp]
    public async Task LoadUnchangedCapture()
    {
        NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "l2-genesis.json")), (NativeSystemTxFormat)version);
        NativeL1Rules l1 = NativeL1Rules.LoadEthereum(File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "l1-genesis.json")));
        _profile = new NativeStreamingProfile(genesis, l1);
        _executor = new StreamingBlockExecutor(_profile, LimboLogs.Instance);
        byte[] request = File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "request.v2.protobuf"));
        Assert.That(ValueKeccak.Compute(request), Is.EqualTo(new ValueHash256(version == 6
            ? "0xb2e42e07bef03c231f66973cd43afad57f3c5720d6ce7eb9e391a0e391dd3278"
            : version == 4 ? "0x4fe2bd2df0167990164d7004a44e141768ecd70595db718dade3e18c054ff715"
            : "0xaefdb086b271c4be7f8c09155236994a2f71cd797e9d7232373f415fdae93459")));
        using MemoryStream stream = new(request, writable: false);
        while (stream.Position < stream.Length)
        {
            ClientFrame frame = ClientFrame.Parser.ParseDelimitedFrom(stream);
            _frames.Add(frame);
            if (frame.Block is not null) _blocks.Add(frame.Block.Data);
        }
        Assert.That(_blocks, Has.Count.EqualTo(30));
        await Program.InitializeExecution(genesis.Rules, TxDecoder.Instance);
    }

    [OneTimeTearDown]
    public void RestoreRecordedDecoder()
    {
        TxDecoder.Instance.RegisterDecoder(new EezNativeSystemTxDecoder());
        TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder());
    }

    [Test]
    public async Task InitializeExecution_FreshDecoder_RegistersTheCapturedNativeHistoryEnvelope()
    {
        TxDecoder<Transaction> decoder = new FreshDecoder();
        byte[] envelope = [];
        foreach (BlockWitness input in _blocks)
        {
            Block block = Rlp.Decode<Block>(input.Rlp.ToByteArray())!;
            foreach (Transaction transaction in block.Transactions)
                if (transaction is NativeHistoryTransaction)
                {
                    envelope = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
                    break;
                }
            if (envelope.Length != 0) break;
        }
        Assert.That(envelope, Is.Not.Empty, "precondition: the capture contains a native history transaction");
        Assert.That(() => decoder.Decode(envelope, RlpBehaviors.SkipTypedWrapping), Throws.TypeOf<RlpException>(),
            "precondition: a fresh decoder has no native history registration");
        await Program.InitializeExecution(_profile.Genesis.Rules, decoder);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder.Decode(envelope, RlpBehaviors.SkipTypedWrapping), Is.TypeOf<NativeHistoryTransaction>());
            Assert.That(KzgPolynomialCommitments.IsInitialized, Is.True, "startup must load KZG before accepting blob-backed finalization");
        }
    }

    [Test]
    public async Task Finalize_CurrentNativeCapture_MatchesIndependentProofAndRejectsPreviousProfile()
    {
        using JsonDocument identity = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "identity.json")));
        JsonElement fields = identity.RootElement;
        EezSettlementContext context = new(fields.GetProperty("rollup_id").GetUInt64(), _profile.Genesis.Specs.ChainId,
            new Address(fields.GetProperty("proof_system").GetString()!),
            new ValueHash256(fields.GetProperty("vkey").GetString()!), _profile.Genesis.Rules.Schedule!.BlockIntervalSeconds);
        using PrivateKey key = new("8b3a350cf5c34c9194ca85829a2df0ec3153be0318b5e2d3348e872092edffba");
        EezAttestationSigner signer = new(key);
        Address registry = new(fields.GetProperty("registry_address").GetString()!);
        Address manager = new(fields.GetProperty("rollup_manager_address").GetString()!);
        if (version == 3)
        {
            Assert.That(() => new NativeOfflineFinalizer(_profile, context, registry, manager, signer, LimboLogs.Instance),
                Throws.ArgumentException, "a previous runtime cannot enter current offline finalization");
            return;
        }
        NativeOfflineFinalizer finalizer = new(_profile, context, registry, manager, signer, LimboLogs.Instance);
        StreamingRegistryLimits limits = new(2, 2, 64 * 1024 * 1024, TimeSpan.FromMinutes(10));
        using StreamingSessionRegistry sessions = new(limits, new StreamingSessionLimits(64, 4 * 1024 * 1024, 100_000),
            TimeProvider.System, StreamingSettlementProfile.RollupXNative, _profile);
        StreamingConnection connection = new(sessions, "composer", context.RollupId);
        ServerFrame previous = connection.Dispatch(_frames[0].Clone()).Response!;
        foreach (BlockWitness block in _blocks)
        {
            ClientFrame frame = new()
            {
                SessionId = previous.SessionId,
                Epoch = previous.Epoch,
                RequestId = block.Number + 1,
                Block = new ProveBlock { Data = block.Clone() }
            };
            SessionOperation append = connection.Dispatch(frame).Operation!;
            try { previous = connection.CommitBlock(append, _executor.Execute(append, CancellationToken.None)); }
            finally { connection.FinishOperation(append); }
        }
        ClientFrame request = _frames[^1].Clone();
        request.SessionId = previous.SessionId;
        request.Epoch = previous.Epoch;
        StreamingDispatch dispatched = connection.Dispatch(request);
        Assert.That(dispatched.Operation, Is.Not.Null, "the configured native route must reach the real finalizer");
        SessionOperation operation = dispatched.Operation!;
        try
        {
            Proof proof = await finalizer.Finalize(operation, CancellationToken.None);
            ServerFrame completed = connection.CompleteProof(operation, proof);
            using JsonDocument response = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "response.json")));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(completed.Proof, Is.EqualTo(proof), "the connection must publish the validated proof");
                Assert.That(Convert.ToHexStringLower(proof.PublicInputsHash.Span), Is.EqualTo(response.RootElement.GetProperty("public_inputs_hash").GetString()),
                    "the native finalizer must reproduce the unchanged independently generated digest");
                Assert.That(Convert.ToHexStringLower(proof.Proof_.Span), Is.EqualTo(response.RootElement.GetProperty("signature").GetString()),
                    "the native finalizer must reproduce the unchanged independently generated signature");
            }
        }
        finally { connection.FinishOperation(operation); }
    }

    [Test]
    public void Append_UnchangedUpstreamCapture_ValidatesEveryBlockAndPublishesEvidenceWithItsAck()
    {
        StreamingSession session = Session();
        int headers = 0;
        foreach (BlockWitness input in _blocks)
        {
            ulong previous = session.Ready(session.Binding, 1).Ready.ValidatedThrough;
            using SessionOperation operation = Admit(session, input);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(session.Ready(session.Binding, 2).Ready.ValidatedThrough, Is.EqualTo(previous));
                Assert.That(operation.NativeHistoryLength, Is.EqualTo(headers));
            }
            ExecutedStreamingBlock result = _executor.Execute(operation, CancellationToken.None);
            headers += input.Witness.L1HeadersRlp.Count;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.NativeHistory!.Count, Is.EqualTo(headers));
                Assert.That(session.Ready(session.Binding, 3).Ready.ValidatedThrough, Is.EqualTo(previous), "execution alone cannot acknowledge a staged prefix");
            }
            ServerFrame ack = session.CommitBlock(operation, result);
            Block recorded = Rlp.Decode<Block>(input.Rlp.ToByteArray())!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ack.Validated.Number, Is.EqualTo(input.Number));
                Assert.That(ack.Validated.Hash, Is.EqualTo(input.Hash));
                Assert.That(new ValueHash256(ack.Validated.PostStateRoot.Span), Is.EqualTo(recorded.Header.StateRoot!.ValueHash256));
                Assert.That(ack.Validated.Reused, Is.False);
            }
        }
        Assert.That(headers, Is.GreaterThan(1), "the capture must extend its checked L1 evidence");
    }

    [Test]
    public async Task Transport_UsesTheRealNativeBackendForEveryCapturedBlock([Values] bool unavailableFinalize)
    {
        StreamingRegistryLimits limits = new(2, 2, 64 * 1024 * 1024, TimeSpan.FromMinutes(10));
        using StreamingSessionRegistry registry = new(limits, new StreamingSessionLimits(64, 4 * 1024 * 1024, 100_000),
            TimeProvider.System, StreamingSettlementProfile.RollupXNative, _profile);
        EezAttestationSigner signer = new(AttesterServer.AttesterKey);
        EezSettlementContext context = new(_frames[0].Begin.RollupId, _profile.Genesis.Specs.ChainId, TestItem.AddressA,
            TestItem.KeccakA.ValueHash256, 1);
        AttesterOptions options = new(new IPEndPoint(IPAddress.Loopback, 0), "", context.RollupId, context.VerificationKey,
            signer.Address, context.ProofSystem, "", "", 1, context.GasLimit, new WindowLimits(64, 4 * 1024 * 1024, 100_000),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        AttestationPipeline pipeline = new(_profile.Genesis.Specs, context, signer, LimboLogs.Instance, _profile.Genesis.Rules);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        await using WebApplication app = Program.Build(builder, options, pipeline);
        BackendAdmission admission = app.Services.GetRequiredService<BackendAdmission>();
        StreamingTransportTests.Reader input = new();
        StreamingTransportTests.Writer output = new();
        TaskCompletionSource<ServerFrame>[] acknowledgments = new TaskCompletionSource<ServerFrame>[_blocks.Count + 1];
        for (int index = 0; index < acknowledgments.Length; index++)
            acknowledgments[index] = new TaskCompletionSource<ServerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.OnWrite = frame =>
        {
            if (frame.Ready is not null) acknowledgments[0].TrySetResult(frame.Clone());
            else if (frame.Validated is { } validated) acknowledgments[checked((int)validated.Number)].TrySetResult(frame.Clone());
            return Task.CompletedTask;
        };
        input.Add(() => _frames[0].Clone());
        for (int index = 0; index < _blocks.Count; index++)
        {
            int position = index;
            input.AddAsync(async () =>
            {
                ServerFrame previous = await acknowledgments[position].Task.WaitAsync(TimeSpan.FromSeconds(30));
                return new ClientFrame
                {
                    SessionId = previous.SessionId,
                    Epoch = previous.Epoch,
                    RequestId = (ulong)position + 2,
                    Block = new ProveBlock { Data = _blocks[position].Clone() }
                };
            });
        }
        if (unavailableFinalize)
            input.AddAsync(async () =>
            {
                ServerFrame previous = await acknowledgments[^1].Task.WaitAsync(TimeSpan.FromSeconds(30));
                ClientFrame finalize = _frames[^1].Clone();
                finalize.SessionId = previous.SessionId;
                finalize.Epoch = previous.Epoch;
                finalize.RequestId = 90;
                return finalize;
            });
        StreamingTransport transport = new(registry, admission, _executor,
            new StreamingTransportLimits(2, 4 * 1024 * 1024, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            _frames[0].Begin.RollupId, NullLogger<StreamingTransport>.Instance);
        await transport.Run(input, output, "composer", CancellationToken.None);
        Assert.That(output.Frames, Has.Count.EqualTo(_blocks.Count + 1 + (unavailableFinalize ? 1 : 0)));
        for (int index = 0; index < _blocks.Count; index++)
        {
            ServerFrame ack = output.Frames[index + 1];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ack.KindCase, Is.EqualTo(ServerFrame.KindOneofCase.Validated));
                Assert.That(ack.Validated.Hash, Is.EqualTo(_blocks[index].Hash));
                Assert.That(ack.Validated.Number, Is.EqualTo(_blocks[index].Number));
            }
        }
        if (unavailableFinalize)
        {
            ServerFrame rejected = output.Frames[^1];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rejected.Rejected.Code, Is.EqualTo((int)StatusCode.Unimplemented));
                Assert.That(rejected.Rejected.Message, Is.EqualTo(version == 3 ? "native settlement authorization unavailable" : "settlement authorization unavailable"));
                Assert.That(rejected.Rejected.ValidatedThrough, Is.EqualTo(_blocks[^1].Number));
                Assert.That(rejected.Rejected.ValidatedHash, Is.EqualTo(_blocks[^1].Hash));
            }
            StreamingConnection resumed = new(registry, "composer", context.RollupId);
            ServerFrame ready = resumed.Dispatch(new ClientFrame
            {
                SessionId = rejected.SessionId,
                RequestId = 91,
                Resume = new Resume()
            }).Response!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ready.Ready.ValidatedThrough, Is.EqualTo(_blocks[^1].Number));
                Assert.That(ready.Ready.ValidatedHash, Is.EqualTo(_blocks[^1].Hash));
            }
        }
    }

    [Test]
    public void FailedExecutionOrHeaderSegment_CannotPublishStagedEvidence([Values("execution", "headers")] string invalid)
    {
        StreamingSession session = Session();
        BlockWitness changed = _blocks[0].Clone();
        if (invalid == "execution") changed.Rlp = ByteString.CopyFrom([0xff]);
        else changed.Witness.L1HeadersRlp.Add(ByteString.CopyFrom([0xc0]));
        using (SessionOperation rejected = Admit(session, changed))
            AssertCode(() => _executor.Execute(rejected, CancellationToken.None), StatusCode.FailedPrecondition);
        Assert.That(session.Ready(session.Binding, 1).Ready.ValidatedThrough, Is.EqualTo(_frames[0].Begin.AnchorNumber));
        using SessionOperation retry = Admit(session, _blocks[0]);
        Assert.That(retry.NativeHistory!.Count, Is.Zero, "a failed segment or block cannot poison the retained chain");
        Assert.That(session.CommitBlock(retry, _executor.Execute(retry, CancellationToken.None)).Validated.Hash, Is.EqualTo(_blocks[0].Hash));
    }

    [Test]
    public void ResumeRewindAndCancel_FencePendingNativeEvidence([Values("resume", "rewind", "cancel")] string control)
    {
        StreamingSession session = Session();
        int target = 1;
        while (_blocks[target].Witness.L1HeadersRlp.Count == 0) target++;
        int before = 0;
        for (int index = 0; index < target; index++)
        {
            Append(session, _blocks[index]);
            before += _blocks[index].Witness.L1HeadersRlp.Count;
        }
        using SessionOperation pending = Admit(session, _blocks[target]);
        ExecutedStreamingBlock result = _executor.Execute(pending, CancellationToken.None);
        Assert.That(result.NativeHistory!.Count, Is.GreaterThan(before));
        SessionBinding old = session.Binding;
        switch (control)
        {
            case "resume": session.Resume("composer"); break;
            case "rewind": session.Rewind(old, 90, _blocks[target - 1].Hash); break;
            default: session.Cancel(old, 90); break;
        }
        AssertCode(() => session.CommitBlock(pending, result), StatusCode.Aborted);
        if (control == "cancel")
        {
            Assert.That(session.CanRelease, Is.False);
            pending.Dispose();
            Assert.That(session.CanRelease, Is.True);
        }
        else
        {
            AssertCode(() => Admit(session, _blocks[target]), StatusCode.ResourceExhausted);
            pending.Dispose();
            using SessionOperation retry = Admit(session, _blocks[target]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry.NativeHistoryLength, Is.EqualTo(before));
                Assert.That(retry.NativeHistory!.Count, Is.EqualTo(before));
            }
            Assert.That(session.CommitBlock(retry, _executor.Execute(retry, CancellationToken.None)).Validated.Number, Is.EqualTo(_blocks[target].Number));
        }
    }

    [Test]
    public void Rewind_UsesTheAcknowledgedL1CheckpointWhenReplayingTheDroppedSegment()
    {
        StreamingSession session = Session();
        int target = 1;
        while (_blocks[target].Witness.L1HeadersRlp.Count == 0) target++;
        int before = 0;
        for (int index = 0; index < target; index++)
        {
            Append(session, _blocks[index]);
            before += _blocks[index].Witness.L1HeadersRlp.Count;
        }
        Append(session, _blocks[target]);
        session.Rewind(session.Binding, 90, _blocks[target - 1].Hash);
        session.Resume("composer");
        using SessionOperation replay = Admit(session, _blocks[target]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replay.NativeHistoryLength, Is.EqualTo(before));
            Assert.That(replay.NativeHistory!.Count, Is.GreaterThan(before), "the immutable bridge may retain a validated suffix while the execution cursor rewinds");
        }
        ExecutedStreamingBlock result = _executor.Execute(replay, CancellationToken.None);
        Assert.That(result.NativeHistory!.Count, Is.EqualTo(before + _blocks[target].Witness.L1HeadersRlp.Count));
        Assert.That(session.CommitBlock(replay, result).Validated.Hash, Is.EqualTo(_blocks[target].Hash));
    }

    [Test]
    public void Duplicate_RequiresAllCheckedWitnessFieldsToMatch([Values("state", "codes", "keys", "headers", "l1")] string field)
    {
        StreamingSession session = Session();
        Append(session, _blocks[0]);
        BlockWitness changed = _blocks[0].Clone();
        ByteString extra = ByteString.CopyFrom([1]);
        switch (field)
        {
            case "state": changed.Witness.State.Add(extra); break;
            case "codes": changed.Witness.Codes.Add(extra); break;
            case "keys": changed.Witness.Keys.Add(extra); break;
            case "headers": changed.Witness.Headers.Add(extra); break;
            default: changed.Witness.L1HeadersRlp.Add(extra); break;
        }
        AssertCode(() => Admit(session, changed), StatusCode.FailedPrecondition);
        Assert.That(session.Ready(session.Binding, 2).Ready.ValidatedHash, Is.EqualTo(_blocks[0].Hash));
    }

    [Test]
    public void Field5Quota_RejectsOneBelowTheCapturedWitness([Values] bool completeQuota)
    {
        long items = version == 6 ? 38 : 49;
        long maxItems = completeQuota ? items : items - 1;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Items(_blocks[0]), Is.EqualTo(items));
            Assert.That(_blocks[0].Witness.L1HeadersRlp, Has.Count.EqualTo(1));
        }
        StreamingSession session = Session(new StreamingSessionLimits(64, 4 * 1024 * 1024, maxItems));
        if (!completeQuota) AssertCode(() => Admit(session, _blocks[0]), StatusCode.ResourceExhausted);
        else Assert.That(Append(session, _blocks[0]).Validated.Hash, Is.EqualTo(_blocks[0].Hash));
    }

    [Test]
    public void NativeReservation_Rejects7999AndAccepts8000Bytes()
    {
        StreamingSessionLimits session = new(64, 1000, 100_000);
        Assert.That(() =>
        {
            using (new StreamingSessionRegistry(new StreamingRegistryLimits(2, 2, 7999, TimeSpan.FromMinutes(10)), session,
                TimeProvider.System, StreamingSettlementProfile.RollupXNative, _profile)) { }
        }, Throws.TypeOf<ArgumentOutOfRangeException>());
        using (new StreamingSessionRegistry(new StreamingRegistryLimits(2, 2, 8000, TimeSpan.FromMinutes(10)), session,
            TimeProvider.System, StreamingSettlementProfile.RollupXNative, _profile)) { }
    }

    [Test]
    public void Duplicate_AtFullQuota_ReusesTheValidatedArtifactWithoutAppendingL1Evidence([Values] bool byteQuota)
    {
        long bytes = byteQuota ? _frames[0].CalculateSize() + FrameSize(_blocks[0]) : 4 * 1024 * 1024;
        long items = byteQuota ? 100_000 : Items(_blocks[0]);
        StreamingSession session = Session(new StreamingSessionLimits(64, bytes, items));
        Append(session, _blocks[0]);
        using (SessionOperation duplicate = Admit(session, _blocks[0]))
        {
            Assert.That(duplicate.ReusedBlock, Is.Not.Null);
            ExecutedStreamingBlock cached = _executor.Execute(duplicate, CancellationToken.None);
            Assert.That(cached, Is.SameAs(duplicate.ReusedBlock));
            Assert.That(session.CommitBlock(duplicate, cached).Validated.Reused, Is.True);
        }
        AssertCode(() => Admit(session, _blocks[1]), StatusCode.ResourceExhausted);
    }

    [Test]
    public void CompleteProof_KeepsTheL1BridgeAndDroppedPrefixQuota([Values("bytes", "items")] string quota)
    {
        Finalize finalize = new()
        {
            FromBlock = _blocks[1].Number,
            ToBlock = _blocks[1].Number,
            TerminalHash = _blocks[1].Hash,
            Settlement = new SettlementInput { AbiCalldata = ByteString.CopyFrom([1]) }
        };
        ClientFrame frame = new() { SessionId = ByteString.CopyFrom(new byte[32]), Epoch = 1, RequestId = 90, Finalize = finalize };
        long maxBytes = quota == "bytes" ? _frames[0].CalculateSize() + FrameSize(_blocks[0]) + FrameSize(_blocks[1]) + frame.CalculateSize() : 4 * 1024 * 1024;
        long maxItems = quota == "items" ? Items(_blocks[0]) + Items(_blocks[1]) : 100_000;
        StreamingSession session = Session(new StreamingSessionLimits(64, maxBytes, maxItems));
        Append(session, _blocks[0]);
        Append(session, _blocks[1]);
        using (SessionOperation operation = session.AdmitFinalize(session.Binding, 90, finalize, frame.CalculateSize()))
        {
            Assert.That(operation.NativeHistory!.Count, Is.EqualTo(_blocks[0].Witness.L1HeadersRlp.Count));
            // Only the session quota transition is under test; no settlement authorization or signing is claimed.
            session.CompleteProof(operation, new Proof { PublicInputsHash = ByteString.CopyFrom(new byte[32]), Proof_ = ByteString.CopyFrom([1]) });
        }
        AssertCode(() => Admit(session, _blocks[2]), StatusCode.ResourceExhausted);
        Assert.That(session.Rewind(session.Binding, 91, _blocks[0].Hash).Ready.ValidatedThrough, Is.EqualTo(_blocks[0].Number));
    }

    [Test]
    public void BackendMustReturnCheckedEvidenceAndUseTheRegistryProfile()
    {
        StreamingSession session = Session();
        using SessionOperation operation = Admit(session, _blocks[0]);
        ExecutedStreamingBlock result = _executor.Execute(operation, CancellationToken.None);
        AssertCode(() => session.CommitBlock(operation, result with { NativeHistory = null }), StatusCode.FailedPrecondition);
        Assert.That(session.Ready(session.Binding, 1).Ready.ValidatedThrough, Is.EqualTo(_frames[0].Begin.AnchorNumber));
        NativeL1Rules l1 = NativeL1Rules.LoadEthereum(File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "l1-genesis.json")));
        StreamingBlockExecutor wrongOwner = new(new NativeStreamingProfile(_profile.Genesis, l1), LimboLogs.Instance);
        AssertCode(() => wrongOwner.Execute(operation, CancellationToken.None), StatusCode.FailedPrecondition);
        Assert.That(session.CommitBlock(operation, result).Validated.Hash, Is.EqualTo(_blocks[0].Hash));
    }

    [Test]
    public void NativeRegistryRequiresItsOwnedConfiguration_AndBindsTheFreshHistoryRuntime()
    {
        StreamingRegistryLimits limits = new(2, 2, 64 * 1024 * 1024, TimeSpan.FromMinutes(10));
        StreamingSessionLimits sessionLimits = new(64, 4 * 1024 * 1024, 100_000);
        Assert.That(() => new StreamingSession("composer", _frames[0].Begin, sessionLimits,
            new StreamingProtocolRules(StreamingSettlementProfile.RollupXNative, HasNativeHistory: true), _frames[0].CalculateSize()), Throws.ArgumentException);
        Assert.That(() => { using (new StreamingSessionRegistry(limits, sessionLimits, TimeProvider.System, StreamingSettlementProfile.Rollup0Calldata, _profile)) { } }, Throws.ArgumentException);
        using StreamingSessionRegistry registry = new(limits, sessionLimits, TimeProvider.System, StreamingSettlementProfile.RollupXNative, _profile);
        StreamingSession session = registry.Begin("composer", _frames[0].Begin, _frames[0].CalculateSize());
        Assert.That(Append(session, _blocks[0]).Validated.Hash, Is.EqualTo(_blocks[0].Hash));
        NativeL1Rules l1 = NativeL1Rules.LoadEthereum(File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "l1-genesis.json")));
        Assert.That(l1.Profile(_profile.Genesis).HistoryCodeHash,
            Is.EqualTo(new ValueHash256(version == 6
                ? "0x3725e8abaa83c431d55a24c39d4327791423019e9c8a2702cc68e6201105baf7"
                : version == 4 ? "0xbe389fdaa7ed5833a65c0ceffacb8e04e727127f8faf6e240611272d566b6676"
                : "0xef41c0d934734d41e8eed6c11a19015ea84cd5cce35e52f5fdd5e5fe30d8dac9")), "the profile commits to the unchanged current upstream runtime");
    }

    private StreamingSession Session(StreamingSessionLimits? limits = null) => new("composer", _frames[0].Begin,
        limits ?? new StreamingSessionLimits(64, 4 * 1024 * 1024, 100_000),
        new StreamingProtocolRules(StreamingSettlementProfile.RollupXNative, HasNativeHistory: true), _frames[0].CalculateSize(), _profile);

    private static long Items(BlockWitness block) => (long)block.Witness.State.Count + block.Witness.Codes.Count
        + block.Witness.Keys.Count + block.Witness.Headers.Count + block.Witness.L1HeadersRlp.Count;

    private static int FrameSize(BlockWitness block) => new ClientFrame
    {
        SessionId = ByteString.CopyFrom(new byte[32]),
        Epoch = 1,
        RequestId = 2,
        Block = new ProveBlock { Data = block }
    }.CalculateSize();

    private static SessionOperation Admit(StreamingSession session, BlockWitness input) =>
        session.AdmitBlock(session.Binding, input.Number + 1, input, FrameSize(input));

    private ServerFrame Append(StreamingSession session, BlockWitness input)
    {
        using SessionOperation operation = Admit(session, input);
        return session.CommitBlock(operation, _executor.Execute(operation, CancellationToken.None));
    }

    private static void AssertCode(Action action, StatusCode code) => Assert.That(Assert.Throws<RpcException>(action)!.StatusCode, Is.EqualTo(code));
    private sealed class FreshDecoder() : TxDecoder<Transaction>(static () => new Transaction());

}
