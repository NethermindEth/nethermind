// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class StreamingRegistryTests
{
    [Test]
    public void OwnerQuota_DoesNotConsumeOtherOwnersCapacity()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime(), 4, 1, 8000);
        registry.Begin("first", Anchor());
        AssertCode(() => registry.Begin("first", Anchor()), StatusCode.ResourceExhausted);
        Assert.That(registry.Begin("second", Anchor()).Id.Length, Is.EqualTo(32));
    }

    [Test]
    public void RegistryCountLimit_IsIndependentOfByteReservation()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime(), 1, 4, 10000);
        registry.Begin("first", Anchor());
        AssertCode(() => registry.Begin("second", Anchor()), StatusCode.ResourceExhausted);
    }

    [Test]
    public void GlobalReservationLimit_IsIndependentOfSessionCount()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime(), 4, 4, 3999);
        registry.Begin("first", Anchor());
        AssertCode(() => registry.Begin("second", Anchor()), StatusCode.ResourceExhausted);
    }

    [Test]
    public void Expiry_RetiresIdleSession_AndReleasesReservation()
    {
        ManualTime time = new();
        using StreamingSessionRegistry registry = Registry(time, 1, 1, 2000);
        StreamingSession old = registry.Begin("first", Anchor());
        time.Advance(TimeSpan.FromSeconds(10));
        AssertCode(() => registry.Resume("first", old.Id, 1), StatusCode.NotFound);
        Assert.That(registry.Begin("first", Anchor()).Id, Is.Not.EqualTo(old.Id));
    }

    [Test]
    public void Expiry_FencesWorker_ButDoesNotReleaseQuotaBeforeDrain()
    {
        ManualTime time = new();
        using StreamingSessionRegistry registry = Registry(time, 1, 1, 2000);
        StreamingSession old = registry.Begin("first", Anchor());
        using SessionOperation operation = old.AdmitBlock(old.Binding, 1, Block());
        time.Advance(TimeSpan.FromSeconds(10));
        AssertCode(() => old.CommitBlock(operation, Executed(operation)), StatusCode.Aborted);
        AssertCode(() => registry.Begin("second", Anchor()), StatusCode.ResourceExhausted);
        operation.Dispose();
        Assert.That(registry.Begin("second", Anchor()).Id.Length, Is.EqualTo(32));
    }

    [Test]
    public void Touch_ExtendsRetention_ButWrongOwnerCannotRefresh()
    {
        ManualTime time = new();
        using StreamingSessionRegistry registry = Registry(time, 1, 1, 2000);
        StreamingSession session = registry.Begin("first", Anchor());
        time.Advance(TimeSpan.FromSeconds(9));
        registry.Touch(session, session.Binding);
        time.Advance(TimeSpan.FromSeconds(9));
        AssertCode(() => registry.Resume("other", session.Id, 1), StatusCode.NotFound);
        Assert.That(session.Ready(session.Binding, 1).Ready.ValidatedThrough, Is.EqualTo(10));
        time.Advance(TimeSpan.FromSeconds(1));
        AssertCode(() => registry.Resume("first", session.Id, 1), StatusCode.NotFound);
    }

    [Test]
    public void Cancel_ReleasesIdleSlot_ButPendingWorkerKeepsReservation()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime(), 1, 1, 2000);
        StreamingSession session = registry.Begin("first", Anchor());
        using SessionOperation pending = session.AdmitBlock(session.Binding, 1, Block());
        session.Cancel(session.Binding, 2);
        AssertCode(() => registry.Begin("second", Anchor()), StatusCode.ResourceExhausted);
        pending.Dispose();
        StreamingSession second = registry.Begin("second", Anchor());
        second.Cancel(second.Binding, 1);
        Assert.That(registry.Begin("third", Anchor()).Id.Length, Is.EqualTo(32));
    }

    [Test]
    public void Dispose_FencesWorkers_AndRejectsFurtherAccess()
    {
        ManualTime time = new();
        StreamingSessionRegistry registry = Registry(time, 1, 1, 2000);
        StreamingSession session = registry.Begin("first", Anchor());
        using SessionOperation pending = session.AdmitBlock(session.Binding, 1, Block());
        registry.Dispose();
        registry.Dispose();
        AssertCode(() => session.CommitBlock(pending, Executed(pending)), StatusCode.Aborted);
        Assert.Throws<ObjectDisposedException>(() => registry.Begin("first", Anchor()));
        Assert.That(time.TimerDisposed, Is.True);
    }

    [Test]
    public void Connection_CorrelatesUnknownOperation_AndPreservesProgress()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime());
        StreamingConnection connection = new(registry, "first", 1);
        ServerFrame unbound = connection.Dispatch(new ClientFrame { RequestId = 4 }).Response!;
        Assert.That((unbound.RequestId, unbound.Epoch, unbound.SessionId.IsEmpty, unbound.Rejected.Code),
            Is.EqualTo((4UL, 0UL, true, (int)StatusCode.Unimplemented)));
        ServerFrame ready = connection.Dispatch(new ClientFrame { RequestId = 5, Begin = Anchor() }).Response!;
        ServerFrame rejected = connection.Dispatch(Frame(ready, 6)).Response!;
        Assert.That((rejected.RequestId, rejected.SessionId, rejected.Epoch, rejected.Rejected.ValidatedThrough, rejected.Rejected.ValidatedHash),
            Is.EqualTo((6UL, ready.SessionId, ready.Epoch, 10UL, Hash(1))));
    }

    [TestCase(0UL, TestName = "Connection_ZeroRollup_ReturnsUnsupportedProfile")]
    [TestCase(2UL, TestName = "Connection_UnknownRollup_ReturnsUnsupportedProfile")]
    public void Connection_UnsupportedRollup_RemainsUnboundAndAllowsCorrectBegin(ulong rollup)
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime(), 1, 1, 2000);
        StreamingConnection connection = new(registry, "first", 1);
        Begin begin = Anchor();
        begin.RollupId = rollup;
        ServerFrame rejected = connection.Dispatch(new ClientFrame { RequestId = 7, Begin = begin }).Response!;
        Assert.That((rejected.RequestId, rejected.SessionId.IsEmpty, rejected.Epoch, rejected.Rejected.Code, rejected.Rejected.ValidatedHash.IsEmpty),
            Is.EqualTo((7UL, true, 0UL, (int)StatusCode.Unimplemented, true)), "unsupported rollups never obtain a binding or consume session capacity");
        Assert.That(connection.Dispatch(new ClientFrame { RequestId = 8, Begin = Anchor() }).Response!.Ready.ValidatedThrough,
            Is.EqualTo(10), "a valid Begin succeeds after the unsupported request");
    }

    [TestCase("Block", TestName = "Connection_BlockBeforeBegin_ReturnsMalformedEnvelope")]
    [TestCase("Finalize", TestName = "Connection_FinalizeBeforeBegin_ReturnsMalformedEnvelope")]
    [TestCase("Rewind", TestName = "Connection_RewindBeforeBegin_ReturnsMalformedEnvelope")]
    [TestCase("Cancel", TestName = "Connection_CancelBeforeBegin_ReturnsMalformedEnvelope")]
    public void Connection_OperationBeforeBegin_ReturnsUnboundRejection(string kind)
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime(), 1, 1, 2000);
        StreamingConnection connection = new(registry, "first", 1);
        ClientFrame frame = new() { RequestId = 7 };
        switch (kind)
        {
            case "Block": frame.Block = new Nethermind.Eez.Prove.V2.Block { Data = Block() }; break;
            case "Finalize": frame.Finalize = new Finalize(); break;
            case "Rewind": frame.Rewind = new Rewind { AncestorHash = Hash(1) }; break;
            default: frame.Cancel = new Cancel(); break;
        }
        ServerFrame rejected = connection.Dispatch(frame).Response!;
        Assert.That((rejected.RequestId, rejected.SessionId.IsEmpty, rejected.Epoch, rejected.Rejected.Code),
            Is.EqualTo((7UL, true, 0UL, (int)StatusCode.InvalidArgument)), "operations without Begin or Resume cannot mutate any session");
        Assert.That(connection.Dispatch(new ClientFrame { RequestId = 8, Begin = Anchor() }).Response!.Ready.ValidatedThrough,
            Is.EqualTo(10), "the malformed envelope leaves admission available");
    }

    [Test]
    public void Connection_OnlyResumeBySameTransportOwner_CanTakeAttachment()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime());
        StreamingConnection original = new(registry, "first", 1);
        ServerFrame ready = original.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        StreamingConnection other = new(registry, "other", 1);
        ServerFrame rejected = other.Dispatch(new ClientFrame { SessionId = ready.SessionId, RequestId = 7, Resume = new Resume() }).Response!;
        Assert.That((rejected.SessionId.IsEmpty, rejected.Epoch, rejected.Rejected.Code), Is.EqualTo((true, 0UL, (int)StatusCode.NotFound)));
        StreamingConnection resumed = new(registry, "first", 1);
        ServerFrame newer = resumed.Dispatch(new ClientFrame { SessionId = ready.SessionId, RequestId = 8, Resume = new Resume() }).Response!;
        Assert.That(newer.Epoch, Is.EqualTo(2));
        AssertCode(() => original.Dispatch(Frame(newer, 9)), StatusCode.Aborted);
    }

    [Test]
    public void Connection_RewindFencesWork_AndWorkerFinishesBeforeRetry()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime());
        StreamingConnection connection = new(registry, "first", 1);
        ServerFrame ready = connection.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        ClientFrame block = Frame(ready, 7);
        block.Block = new Nethermind.Eez.Prove.V2.Block { Data = Block() };
        SessionOperation operation = connection.Dispatch(block).Operation!;
        ClientFrame duplicateId = Frame(ready, 7);
        duplicateId.Cancel = new Cancel();
        Assert.That(connection.Dispatch(duplicateId).Response!.Rejected.Code, Is.EqualTo((int)StatusCode.InvalidArgument));
        ClientFrame rewind = Frame(ready, 8);
        rewind.Rewind = new Rewind { AncestorHash = Hash(1) };
        ServerFrame newer = connection.Dispatch(rewind).Response!;
        AssertCode(() => connection.CommitBlock(operation, Executed(operation)), StatusCode.Aborted);
        ClientFrame retry = Frame(newer, 9);
        retry.Block = new Nethermind.Eez.Prove.V2.Block { Data = Block() };
        Assert.That(connection.Dispatch(retry).Response!.Rejected.Code, Is.EqualTo((int)StatusCode.ResourceExhausted));
        connection.FinishOperation(operation);
        SessionOperation next = connection.Dispatch(retry).Operation!;
        Assert.That(connection.CommitBlock(next, Executed(next)).Validated.Number, Is.EqualTo(11));
        connection.FinishOperation(next);
    }

    [Test]
    public void Connection_CancelEndsStream_WithoutReleasingRunningWorker()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime(), 1, 1, 2000);
        StreamingConnection connection = new(registry, "first", 1);
        ServerFrame ready = connection.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        ClientFrame block = Frame(ready, 1);
        block.Block = new Nethermind.Eez.Prove.V2.Block { Data = Block() };
        SessionOperation operation = connection.Dispatch(block).Operation!;
        ClientFrame cancel = Frame(ready, 2);
        cancel.Cancel = new Cancel();
        Assert.That(connection.Dispatch(cancel).EndStream, Is.True);
        AssertCode(() => connection.CommitBlock(operation, Executed(operation)), StatusCode.Aborted);
        AssertCode(() => registry.Begin("second", Anchor()), StatusCode.ResourceExhausted);
        connection.FinishOperation(operation);
        Assert.That(registry.Begin("second", Anchor()).Id.Length, Is.EqualTo(32));
    }

    [Test]
    public void Connection_ResumeWrongRollup_DoesNotFenceOriginalAttachment()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime());
        StreamingConnection original = new(registry, "first", 1);
        ServerFrame ready = original.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        StreamingConnection wrong = new(registry, "first", 2);
        ServerFrame rejected = wrong.Dispatch(new ClientFrame { SessionId = ready.SessionId, RequestId = 7, Resume = new Resume() }).Response!;
        Assert.That((rejected.SessionId.IsEmpty, rejected.Epoch, rejected.Rejected.Code), Is.EqualTo((true, 0UL, (int)StatusCode.NotFound)));
        Assert.That(original.Dispatch(Frame(ready, 8)).Response!.Epoch, Is.EqualTo(1));
    }

    [Test]
    public void Connection_DetachFencesLateWork_AndPreservesSessionForResume()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime());
        StreamingConnection original = new(registry, "first", 1);
        ServerFrame ready = original.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        ClientFrame block = Frame(ready, 1);
        block.Block = new Nethermind.Eez.Prove.V2.Block { Data = Block() };
        SessionOperation pending = original.Dispatch(block).Operation!;
        original.Detach();
        AssertCode(() => original.CommitBlock(pending, Executed(pending)), StatusCode.Aborted);
        original.FinishOperation(pending);
        StreamingConnection resumed = new(registry, "first", 1);
        ServerFrame newer = resumed.Dispatch(new ClientFrame { SessionId = ready.SessionId, Resume = new Resume() }).Response!;
        Assert.That((newer.Epoch, newer.Ready.ValidatedThrough), Is.EqualTo((2UL, 10UL)));
    }

    [Test]
    public void SubmillisecondRetention_IsRejectedBeforeTimerCreation() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingSessionRegistry(
            new StreamingRegistryLimits(1, 1, 2000, TimeSpan.FromTicks(1)),
            new StreamingSessionLimits(8, 1000, 16), new ManualTime()));

    [Test]
    public void GlobalReservation_AtLimitAcceptsExactlyTwoSessions()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime(), 4, 4, 4000);
        registry.Begin("first", Anchor());
        registry.Begin("second", Anchor());
        AssertCode(() => registry.Begin("third", Anchor()), StatusCode.ResourceExhausted);
    }

    [TestCase("id")]
    [TestCase("epoch")]
    public void Connection_WrongFrameBinding_DoesNotChangeProgress(string wrong)
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime());
        StreamingConnection connection = new(registry, "first", 1);
        ServerFrame ready = connection.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        ClientFrame cancel = Frame(ready, 7);
        cancel.Cancel = new Cancel();
        if (wrong == "id") cancel.SessionId = Hash(99);
        else cancel.Epoch++;
        ServerFrame rejected = connection.Dispatch(cancel).Response!;
        Assert.That((rejected.SessionId, rejected.Epoch, rejected.RequestId, rejected.Rejected.Code, rejected.Rejected.ValidatedThrough),
            Is.EqualTo((ready.SessionId, ready.Epoch, 7UL, (int)StatusCode.FailedPrecondition, 10UL)));
        Assert.That(connection.Dispatch(Frame(ready, 8)).Response!.Rejected.Code, Is.EqualTo((int)StatusCode.Unimplemented));
    }

    [Test]
    public void Connection_FinalizeProof_EndsStream_AndKeepsSessionResumable()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime());
        StreamingConnection connection = new(registry, "first", 1);
        ServerFrame ready = connection.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        Commit(connection, ready, 1);
        ClientFrame finalize = Frame(ready, 2);
        finalize.Finalize = new Finalize
        {
            FromBlock = 11,
            ToBlock = 11,
            TerminalHash = Hash(11),
            Settlement = new SettlementInput { AbiCalldata = Hash(50) },
        };
        SessionOperation pending = connection.Dispatch(finalize).Operation!;
        Assert.That(pending.CopyBlocks()[0].Number, Is.EqualTo(11));
        ServerFrame response = connection.CompleteProof(pending, new Proof { PublicInputsHash = Hash(70), Proof_ = Hash(71) });
        Assert.That((response.RequestId, response.Epoch), Is.EqualTo((2UL, 1UL)));
        Assert.Throws<InvalidOperationException>(() => connection.Dispatch(Frame(ready, 3)));
        connection.FinishOperation(pending);
        StreamingConnection resumed = new(registry, "first", 1);
        ServerFrame newer = resumed.Dispatch(new ClientFrame { SessionId = ready.SessionId, Resume = new Resume() }).Response!;
        Assert.That(newer.Ready.ValidatedThrough, Is.EqualTo(11));
    }

    [Test]
    public void Connection_BackendRejection_IsCorrelated_AndAllowsRetryAfterDrain()
    {
        using StreamingSessionRegistry registry = Registry(new ManualTime());
        StreamingConnection connection = new(registry, "first", 1);
        ServerFrame ready = connection.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        ClientFrame block = Frame(ready, 1);
        block.Block = new Nethermind.Eez.Prove.V2.Block { Data = Block() };
        SessionOperation pending = connection.Dispatch(block).Operation!;
        ServerFrame rejected = connection.RejectOperation(pending, new Status(StatusCode.FailedPrecondition, "execution rejected"));
        Assert.That((rejected.RequestId, rejected.Rejected.Code, rejected.Rejected.ValidatedThrough, rejected.Rejected.ValidatedHash),
            Is.EqualTo((1UL, (int)StatusCode.FailedPrecondition, 10UL, Hash(1))));
        connection.FinishOperation(pending);
        Commit(connection, ready, 2);
        ServerFrame unsupported = connection.Dispatch(Frame(ready, 3)).Response!;
        Assert.That((unsupported.Rejected.Code, unsupported.Rejected.ValidatedThrough, unsupported.Rejected.ValidatedHash),
            Is.EqualTo(((int)StatusCode.Unimplemented, 11UL, Hash(11))));
    }

    private static void Commit(StreamingConnection connection, ServerFrame ready, ulong requestId)
    {
        ClientFrame block = Frame(ready, requestId);
        block.Block = new Nethermind.Eez.Prove.V2.Block { Data = Block() };
        SessionOperation operation = connection.Dispatch(block).Operation!;
        connection.CommitBlock(operation, Executed(operation));
        connection.FinishOperation(operation);
    }

    private static StreamingSessionRegistry Registry(ManualTime time, int count = 4, int perOwner = 4, long bytes = 8000) =>
        new(new StreamingRegistryLimits(count, perOwner, bytes, TimeSpan.FromSeconds(10)), new StreamingSessionLimits(8, 1000, 16), time);
    private static Begin Anchor() => new() { RollupId = 1, AnchorNumber = 10, AnchorHash = Hash(1), AnchorStateRoot = Hash(2) };
    private static ClientFrame Frame(ServerFrame ready, ulong requestId) => new() { SessionId = ready.SessionId, Epoch = ready.Epoch, RequestId = requestId };
    private static ByteString Hash(byte value)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(value);
        return ByteString.CopyFrom(bytes);
    }
    private static BlockWitness Block() => new()
    {
        Number = 11,
        Hash = Hash(11),
        ParentHash = Hash(1),
        Rlp = Hash(11),
        Witness = new ExecutionWitness(),
    };
    private static ExecutedStreamingBlock Executed(SessionOperation operation) => new(11, Hash(11), Hash(1), operation.PreStateRoot, Hash(31));
    private static void AssertCode(Action action, StatusCode code) => Assert.That(Assert.Throws<RpcException>(action)!.StatusCode, Is.EqualTo(code));

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        private TimerCallback? _callback;
        private object? _state;
        internal bool TimerDisposed { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            if (!TimerDisposed) _callback?.Invoke(_state);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new ManualTimer(this);
        }
        private sealed class ManualTimer(ManualTime time) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => !time.TimerDisposed;
            public void Dispose() => time.TimerDisposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
