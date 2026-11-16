// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser]
public class SparseBlobPoolPeerRegistryBenchmarks
{
    private const int AnnouncementsPerPeer = 2048;
    private const int ProviderProbabilityPercent = 15;

    private readonly ManualTimerFactory _timerFactory = new();
    private SparseBlobPoolPeerRegistry _registry = null!;
    private BenchmarkPeer[] _peers = null!;
    private ValueHash256[] _hashes = null!;

    [Params(16384)]
    public int TrackedTransactions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        BlobCustodyTracker custodyTracker = new();
        custodyTracker.Update(BlobCellMask.FromIndices([0, 1, 2, 3, 4, 5, 6, 7]));
        _registry = new SparseBlobPoolPeerRegistry(
            NullTxPool.Instance,
            custodyTracker,
            RunImmediatelyScheduler.Instance,
            LimboLogs.Instance,
            TimeSpan.Zero,
            timerFactory: _timerFactory,
            timestamper: new ManualTimestamper());

        _peers = new BenchmarkPeer[(TrackedTransactions + AnnouncementsPerPeer - 1) / AnnouncementsPerPeer];
        for (int i = 0; i < _peers.Length; i++)
        {
            byte[] key = new byte[PublicKey.LengthInBytes];
            BinaryPrimitives.WriteInt32LittleEndian(key, i + 1);
            _peers[i] = new BenchmarkPeer(new PublicKey(key));
            _registry.AddPeer(_peers[i]);
        }

        _hashes = new ValueHash256[TrackedTransactions];
        for (int i = 0; i < _hashes.Length; i++)
        {
            _hashes[i] = ValueKeccak.Compute(BitConverter.GetBytes(i));
            if (!_registry.RecordAnnouncement(_peers[i / AnnouncementsPerPeer], _hashes[i].ToHash256(), BlobCellMask.Full))
            {
                throw new InvalidOperationException("Announcement was not tracked.");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _registry.Dispose();

    [Benchmark]
    public void MaintenanceTick() => _timerFactory.Timer.Fire();

    [Benchmark(OperationsPerInvoke = AnnouncementsPerPeer)]
    public int RecordAnnouncementAndGetRequestMask()
    {
        int requested = 0;
        BenchmarkPeer peer = _peers[0];
        ValueHash256[] hashes = _hashes;
        for (int i = 0; i < AnnouncementsPerPeer; i++)
        {
            Hash256 hash = hashes[i].ToHash256();
            if (_registry.RecordAnnouncement(peer, hash, BlobCellMask.Full))
            {
                requested += _registry.GetRequestMask(hash, BlobCellMask.Full, ProviderProbabilityPercent).Count;
            }
        }

        return requested;
    }

    private sealed class BenchmarkPeer(PublicKey id) : ISparseBlobPoolPeer
    {
        public PublicKey Id { get; } = id;
        public bool IsClosing => false;
        public bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask) => true;
        public bool TrySendPooledTransactionRequest(Hash256 hash) => true;
        public void MaintainSparseBlobState(DateTimeOffset now) { }
        public void DisconnectSparseBlobPeer(DisconnectReason reason, string details) { }
    }

    private sealed class ManualTimerFactory : ITimerFactory
    {
        public ManualTimer Timer { get; } = new();

        public Core.Timers.ITimer CreateTimer(TimeSpan interval) => Timer;
    }

    private sealed class ManualTimer : Core.Timers.ITimer
    {
        public bool AutoReset { get; set; }
        public bool Enabled { get; set; }
        public TimeSpan Interval { get; set; }
        public double IntervalMilliseconds
        {
            get => Interval.TotalMilliseconds;
            set => Interval = TimeSpan.FromMilliseconds(value);
        }

        public event EventHandler Elapsed;

        public void Start() => Enabled = true;
        public void Stop() => Enabled = false;
        public void Dispose() => Enabled = false;
        public void Fire() => Elapsed?.Invoke(this, EventArgs.Empty);
    }
}
