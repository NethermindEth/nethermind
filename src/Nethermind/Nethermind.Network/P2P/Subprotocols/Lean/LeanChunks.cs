// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>A bounded fragment of a whole-wrapper commitment on lean/2.</summary>
public sealed class LeanProofChunkMessage(ValueHash256 wrapperHash, int totalBytes, int index, int count,
    int chunkSize, ReadOnlyMemory<byte> data) : P2PMessage
{
    public const int DefaultChunkSize = 64 * 1024;
    public const int MaxChunkSize = 128 * 1024;
    public const int HeaderSize = 48;
    public override string Protocol => LeanProtocolHandler.Code;
    public override int PacketType => 1;
    public ValueHash256 WrapperHash { get; } = wrapperHash;
    public int TotalBytes { get; } = totalBytes;
    public int Index { get; } = index;
    public int Count { get; } = count;
    public int ChunkSize { get; } = chunkSize;
    public ReadOnlyMemory<byte> Data { get; } = data;

    public static bool IsValidGeometry(int totalBytes, int index, int count, int chunkSize, int dataBytes)
        => totalBytes is > 0 and <= LeanProofStore.MaxWrapperBytes
            && chunkSize is 16 * 1024 or 32 * 1024 or DefaultChunkSize or MaxChunkSize
            && count == (totalBytes + chunkSize - 1) / chunkSize && index >= 0 && index < count
            && dataBytes == Math.Min(chunkSize, totalBytes - index * chunkSize);
}

/// <summary>Checks fragment geometry before copying any peer bytes.</summary>
public sealed class LeanProofChunkMessageSerializer : IZeroMessageSerializer<LeanProofChunkMessage>
{
    public void Serialize(IByteBuffer buffer, LeanProofChunkMessage message)
    {
        if (!LeanProofChunkMessage.IsValidGeometry(message.TotalBytes, message.Index, message.Count,
                message.ChunkSize, message.Data.Length)) throw new ArgumentException("Invalid lean chunk geometry");
        byte[] bytes = new byte[LeanProofChunkMessage.HeaderSize + message.Data.Length];
        message.WrapperHash.Bytes.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32), message.TotalBytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(36), message.Index);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(40), message.Count);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(44), message.ChunkSize);
        message.Data.Span.CopyTo(bytes.AsSpan(LeanProofChunkMessage.HeaderSize));
        buffer.WriteBytes(bytes);
    }

    public LeanProofChunkMessage Deserialize(IByteBuffer buffer)
    {
        if (buffer.ReadableBytes < LeanProofChunkMessage.HeaderSize
            || buffer.ReadableBytes > LeanProofChunkMessage.HeaderSize + LeanProofChunkMessage.MaxChunkSize)
            throw new RlpException("Invalid lean chunk length");
        byte[] header = new byte[LeanProofChunkMessage.HeaderSize];
        buffer.GetBytes(buffer.ReaderIndex, header);
        int total = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(32));
        int index = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(36));
        int count = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(40));
        int size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(44));
        int length = buffer.ReadableBytes - LeanProofChunkMessage.HeaderSize;
        if (!LeanProofChunkMessage.IsValidGeometry(total, index, count, size, length))
            throw new RlpException("Invalid lean chunk geometry");
        buffer.SkipBytes(LeanProofChunkMessage.HeaderSize);
        byte[] data = new byte[length];
        buffer.ReadBytes(data);
        return new(new ValueHash256(header.AsSpan(0, 32)), total, index, count, size, data);
    }
}

/// <summary>Limits incomplete and queued reconstructed wrappers across all peers.</summary>
public sealed class LeanReassemblyBudget
{
    public const int MaxBytes = 64 * 1024 * 1024;
    public const int MaxAssemblies = 64;
    private readonly Lock _lock = new();
    private int _bytes;
    private int _assemblies;
    internal int RetainedBytes { get { lock (_lock) return _bytes; } }
    internal int RetainedAssemblies { get { lock (_lock) return _assemblies; } }

    internal IDisposable? TryRent(int bytes, Action releasePeer)
    {
        lock (_lock)
        {
            if (_assemblies == MaxAssemblies || bytes > MaxBytes - _bytes) return null;
            _assemblies++;
            _bytes += bytes;
        }
        return new Lease(() =>
        {
            lock (_lock) { _assemblies--; _bytes -= bytes; }
            releasePeer();
        });
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>Reassembles peer fragments without trusting their unauthenticated wrapper commitment.</summary>
public sealed class LeanChunkReassembler : IDisposable
{
    public const int MaxPeerAssemblies = 2;
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxAssemblyLifetime = TimeSpan.FromMinutes(5);
    private readonly LeanReassemblyBudget _budget;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, Assembly> _assemblies = [];
    private readonly Dictionary<ValueHash256, long> _recent = [];
    private readonly ITimer _expiry;
    private int _retained;
    private bool _disposed;
    private sealed class Assembly(LeanProofChunkMessage chunk, IDisposable lease, long timestamp)
    {
        public readonly byte[] Bytes = new byte[chunk.TotalBytes];
        public readonly bool[] Seen = new bool[chunk.Count];
        public readonly int ChunkSize = chunk.ChunkSize;
        public readonly IDisposable Lease = lease;
        public int Received;
        public readonly long Created = timestamp;
        public long Updated = timestamp;
    }

    public LeanChunkReassembler(LeanReassemblyBudget budget, TimeProvider? clock = null)
    {
        _budget = budget;
        _clock = clock ?? TimeProvider.System;
        _expiry = _clock.CreateTimer(_ => Expire(), null, InactivityTimeout, InactivityTimeout);
    }

    public LeanProofWrapperMessage? Add(LeanProofChunkMessage chunk)
    {
        if (!LeanProofChunkMessage.IsValidGeometry(chunk.TotalBytes, chunk.Index, chunk.Count, chunk.ChunkSize, chunk.Data.Length))
            throw new RlpException("Invalid lean chunk geometry");
        lock (_lock)
        {
            if (_disposed) return null;
            ExpireCore();
            long now = _clock.GetTimestamp();
            if (_recent.ContainsKey(chunk.WrapperHash)) return null;
            if (!_assemblies.TryGetValue(chunk.WrapperHash, out Assembly? assembly))
            {
                if (_retained == MaxPeerAssemblies) return null;
                IDisposable? lease = _budget.TryRent(chunk.TotalBytes, ReleasePeer);
                if (lease is null) return null;
                _retained++;
                try { assembly = new(chunk, lease, now); }
                catch { lease.Dispose(); throw; }
                _assemblies.Add(chunk.WrapperHash, assembly);
            }
            if (assembly.Bytes.Length != chunk.TotalBytes || assembly.ChunkSize != chunk.ChunkSize)
                throw new RlpException("Inconsistent lean chunk geometry");
            int offset = chunk.Index * chunk.ChunkSize;
            if (assembly.Seen[chunk.Index])
            {
                if (!assembly.Bytes.AsSpan(offset, chunk.Data.Length).SequenceEqual(chunk.Data.Span))
                    throw new RlpException("Conflicting lean chunk");
                return null;
            }
            chunk.Data.Span.CopyTo(assembly.Bytes.AsSpan(offset));
            assembly.Seen[chunk.Index] = true;
            assembly.Updated = now;
            if (++assembly.Received != assembly.Seen.Length) return null;
            _assemblies.Remove(chunk.WrapperHash);
            if (ValueKeccak.Compute(assembly.Bytes) != chunk.WrapperHash)
            {
                assembly.Lease.Dispose();
                throw new RlpException("Lean wrapper hash mismatch");
            }
            if (_recent.Count == 64)
            {
                ValueHash256 oldest = default;
                long oldestTime = long.MaxValue;
                foreach ((ValueHash256 key, long timestamp) in _recent)
                    if (timestamp < oldestTime) { oldest = key; oldestTime = timestamp; }
                _recent.Remove(oldest);
            }
            _recent[chunk.WrapperHash] = now;
            return new(assembly.Bytes, assembly.Lease);
        }
    }

    private void ReleasePeer() { lock (_lock) _retained--; }
    private void Expire() { lock (_lock) ExpireCore(); }
    private void ExpireCore()
    {
        long now = _clock.GetTimestamp();
        List<ValueHash256> expired = [];
        foreach ((ValueHash256 hash, Assembly assembly) in _assemblies)
            if (_clock.GetElapsedTime(assembly.Updated, now) >= InactivityTimeout
                || _clock.GetElapsedTime(assembly.Created, now) >= MaxAssemblyLifetime) expired.Add(hash);
        foreach (ValueHash256 hash in expired)
        {
            Assembly assembly = _assemblies[hash];
            _assemblies.Remove(hash);
            assembly.Lease.Dispose();
        }
        expired.Clear();
        foreach ((ValueHash256 hash, long timestamp) in _recent)
            if (_clock.GetElapsedTime(timestamp, now) >= InactivityTimeout) expired.Add(hash);
        foreach (ValueHash256 hash in expired) _recent.Remove(hash);
    }

    public void Dispose()
    {
        _expiry.Dispose();
        lock (_lock)
        {
            _disposed = true;
            foreach (Assembly assembly in _assemblies.Values) assembly.Lease.Dispose();
            _assemblies.Clear();
            _recent.Clear();
        }
    }
}
