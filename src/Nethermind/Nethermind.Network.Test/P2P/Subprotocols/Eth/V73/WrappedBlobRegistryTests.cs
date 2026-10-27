// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Test;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>Bounds and gating of EIP-8437 wrapped blob transactions in the sparse blob pool registry.</summary>
public class WrappedBlobRegistryTests
{
    private ITxPool _pool = null!;
    private ManualTimestamper _time = null!;
    private ITimer _timer = null!;
    private SparseBlobPoolPeerRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        _pool = Substitute.For<ITxPool>();
        _pool.ValidateTxForBlobSampling(Arg.Any<Transaction>()).Returns(AcceptTxResult.Accepted);
        _time = new ManualTimestamper(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _timer = Substitute.For<ITimer>();
        ITimerFactory timers = Substitute.For<ITimerFactory>();
        timers.CreateTimer(Arg.Any<TimeSpan>()).Returns(_timer);
        _registry = new SparseBlobPoolPeerRegistry(_pool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance,
            TimeSpan.Zero, timerFactory: timers, timestamper: _time);
    }

    [TearDown]
    public void TearDown()
    {
        _registry.Dispose();
        _timer.Dispose();
    }

    private void Maintain(TimeSpan elapsed)
    {
        _time.Add(elapsed);
        _timer.Elapsed += Raise.Event();
    }

    private Peer Connect(int index)
    {
        Peer peer = new(TestItem.PublicKeys[index]);
        _registry.AddPeer(peer);
        return peer;
    }

    // One peer's claimed type never gates another's: cells announced as type-3 may be fetched early even if another peer
    // claimed a frame transaction. Once the body is known its type decides, and a peer that claimed another type is disconnected.
    [Test]
    public void Announced_types_are_per_peer_and_the_body_decides()
    {
        Peer framePeer = Connect(0), blobPeer = Connect(1);
        Transaction blob = BlobFrameTransaction(1);
        Hash256 hash = blob.Hash!;

        Assert.That(_registry.RecordAnnouncement(framePeer, hash, BlobCellMask.Full, TxType.FrameTx), Is.True);
        Assert.That(_registry.TryRequestCells(hash, BlobCellMask.Full, framePeer.Id), Is.False, "frame cells wait for the body");
        Assert.That(_registry.RecordAnnouncement(blobPeer, hash, BlobCellMask.Full, TxType.Blob), Is.True);
        Assert.That(_registry.TryRequestCells(hash, BlobCellMask.Full, framePeer.Id), Is.True, "a type-3 claim is not gated by the other");

        using Pin pin = new();
        Assert.That(_registry.Admit(blob, framePeer, pin), Is.EqualTo(AcceptTxResult.Accepted));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blobPeer.Disconnects.Single().Reason, Is.EqualTo(DisconnectReason.BreachOfProtocol));
            Assert.That(framePeer.Disconnects, Is.Empty);
            Assert.That(_registry.HasRecordedTransaction(hash), Is.True);
        }
    }

    // A body without a validated wrapper never opens cell retrieval for a dependency-bearing frame transaction.
    [Test]
    public void Dependency_bearing_body_outside_a_wrapper_keeps_cells_gated()
    {
        Peer peer = Connect(0);
        Transaction blob = BlobFrameTransaction(1);
        _registry.RecordAnnouncement(peer, blob.Hash!, BlobCellMask.Full, TxType.FrameTx);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_registry.RecordTransaction(peer, blob), Is.EqualTo(AcceptTxResult.MissingDependencyProof));
            Assert.That(_registry.HasRecordedTransaction(blob.Hash!), Is.False);
            Assert.That(_registry.TryRequestCells(blob.Hash!, BlobCellMask.Full, peer.Id), Is.False);
        }
    }

    [Test]
    public void Wrapped_bodies_are_capped_per_sender()
    {
        List<Pin> pins = [];
        for (int i = 0; i < 16; i++)
            Assert.That(_registry.Admit(BlobFrameTransaction(i), null, Track(pins)), Is.EqualTo(AcceptTxResult.Accepted));
        Pin refused = Track(pins);
        Assert.That(_registry.Admit(BlobFrameTransaction(16), null, refused), Is.EqualTo(AcceptTxResult.IncompleteBlobData));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.Disposed, Is.True, "a refused body releases its proof pin");
            Assert.That(_registry.WrappedCount, Is.EqualTo(16));
        }
        _registry.Dispose();
        Assert.That(pins.All(static p => p.Disposed), Is.True);
    }

    [Test]
    public void Wrapped_bodies_are_dropped_with_their_source_peer()
    {
        Peer source = Connect(0), other = Connect(1);
        Pin fromSource = new(), fromOther = new();
        _registry.Admit(BlobFrameTransaction(1), source, fromSource);
        _registry.Admit(BlobFrameTransaction(2), other, fromOther);
        _registry.RemovePeer(source);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromSource.Disposed, Is.True);
            Assert.That(fromOther.Disposed, Is.False);
            Assert.That(_registry.WrappedCount, Is.EqualTo(1));
        }
    }

    // A body nobody announces cells for expires after 30 seconds; one with a provider is kept for the tracked-state lifetime.
    [Test]
    public void Unannounced_wrapped_bodies_expire_sooner()
    {
        Peer peer = Connect(0);
        Transaction announced = BlobFrameTransaction(1), silent = BlobFrameTransaction(2);
        Pin announcedPin = new(), silentPin = new();
        _registry.RecordAnnouncement(peer, announced.Hash!, BlobCellMask.Full, TxType.FrameTx);
        _registry.Admit(announced, peer, announcedPin);
        _registry.Admit(silent, peer, silentPin);

        Maintain(TimeSpan.FromSeconds(31));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(silentPin.Disposed, Is.True);
            Assert.That(announcedPin.Disposed, Is.False);
        }
        Maintain(TimeSpan.FromMinutes(5));
        Assert.That(announcedPin.Disposed, Is.True);
    }

    private static Pin Track(List<Pin> pins)
    {
        Pin pin = new();
        pins.Add(pin);
        return pin;
    }

    private sealed class Pin : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Peer(PublicKey id) : ISparseBlobPoolPeer
    {
        public PublicKey Id { get; } = id;
        public bool IsClosing { get; private set; }
        public List<(DisconnectReason Reason, string Details)> Disconnects { get; } = [];
        public bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask) => true;
        public bool TrySendPooledTransactionRequest(Hash256 hash) => true;
        public void MaintainSparseBlobState(DateTimeOffset now) { }

        public void DisconnectSparseBlobPeer(DisconnectReason reason, string details)
        {
            IsClosing = true;
            Disconnects.Add((reason, details));
        }
    }
}
