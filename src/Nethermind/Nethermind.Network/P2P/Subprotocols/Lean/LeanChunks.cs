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
        buffer.EnsureWritable(LeanProofChunkMessage.HeaderSize + message.Data.Length);
        buffer.WriteBytes(message.WrapperHash.Bytes);
        buffer.WriteInt(message.TotalBytes);
        buffer.WriteInt(message.Index);
        buffer.WriteInt(message.Count);
        buffer.WriteInt(message.ChunkSize);
        if (buffer.HasArray)
        {
            message.Data.Span.CopyTo(buffer.Array.AsSpan(buffer.ArrayOffset + buffer.WriterIndex, message.Data.Length));
            buffer.SetWriterIndex(buffer.WriterIndex + message.Data.Length);
        }
        else buffer.WriteBytes(message.Data.Span);
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

/// <summary>Charges received fragments and completed admission buffers across all peers.</summary>
public sealed class LeanReassemblyBudget
{
    public const int MaxBytes = 64 * 1024 * 1024;
    public const int MaxAssemblies = 1024;
    public const int MaxIncompleteBytes = 48 * 1024 * 1024;
    private readonly Lock _lock = new();
    private int _bytes;
    private int _assemblies;
    private int _incompleteBytes;
    internal int RetainedBytes { get { lock (_lock) return _bytes; } }
    internal int RetainedAssemblies { get { lock (_lock) return _assemblies; } }

    internal Lease? TryRent(int bytes, Action<int> releasePeer, bool incomplete = true)
    {
        lock (_lock)
        {
            if (_assemblies == MaxAssemblies || bytes > MaxBytes - _bytes
                || (incomplete && bytes > MaxIncompleteBytes - _incompleteBytes)) return null;
            _assemblies++;
            _bytes += bytes;
            if (incomplete) _incompleteBytes += bytes;
            return new(this, bytes, incomplete, releasePeer);
        }
    }

    internal sealed class Lease(LeanReassemblyBudget owner, int bytes, bool incomplete, Action<int> releasePeer) : IDisposable
    {
        private int _bytes = bytes;
        private bool _disposed;
        private int _incompleteBytes = incomplete ? bytes : 0;
        internal bool TryGrow(int additionalBytes, bool completion)
        {
            lock (owner._lock)
            {
                if (_disposed || additionalBytes > MaxBytes - owner._bytes
                    || (!completion && additionalBytes > MaxIncompleteBytes - owner._incompleteBytes)) return false;
                owner._bytes += additionalBytes;
                _bytes += additionalBytes;
                if (!completion) { owner._incompleteBytes += additionalBytes; _incompleteBytes += additionalBytes; }
                return true;
            }
        }

        internal void Complete(int releasedBytes)
        {
            lock (owner._lock)
            {
                owner._bytes -= releasedBytes;
                _bytes -= releasedBytes;
                owner._incompleteBytes -= _incompleteBytes;
                _incompleteBytes = 0;
            }
        }

        public void Dispose()
        {
            int releasedBytes;
            lock (owner._lock)
            {
                if (_disposed) return;
                _disposed = true;
                releasedBytes = _bytes;
                owner._assemblies--;
                owner._bytes -= releasedBytes;
                owner._incompleteBytes -= _incompleteBytes;
            }
            releasePeer(releasedBytes);
        }
    }
}

/// <summary>Checks ordered-stream integrity; the peer's wrapper commitment does not authenticate its proof.</summary>
public sealed class LeanChunkReassembler : IDisposable
{
    public const int MaxPeerAssemblies = 2;
    public const int MaxPeerBytes = 2 * LeanProofStore.MaxWrapperBytes + 64 * 1024;
    public const int MaxPeerCompletionBytes = MaxPeerBytes + LeanProofStore.MaxWrapperBytes;
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxAssemblyLifetime = TimeSpan.FromMinutes(5);
    private readonly LeanReassemblyBudget _budget;
    private readonly TimeProvider _clock;
    private readonly Action? _onIncompleteAbuse;
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, Assembly> _assemblies = [];
    private readonly HashSet<ValueHash256> _completed = [];
    private readonly ITimer _expiry;
    private int _retained;
    private int _retainedBytes;
    private int _expiredStreams;
    private bool _disposed;
    private sealed class Assembly(LeanProofChunkMessage chunk, LeanReassemblyBudget.Lease lease, long timestamp)
    {
        public readonly ReadOnlyMemory<byte>[] Chunks = new ReadOnlyMemory<byte>[chunk.Count];
        public readonly int TotalBytes = chunk.TotalBytes;
        public readonly int ChunkSize = chunk.ChunkSize;
        public readonly LeanReassemblyBudget.Lease Lease = lease;
        public readonly int MetadataBytes = chunk.Count * 16;
        public int Received;
        public int ReceivedBytes;
        public readonly long Created = timestamp;
        public long Updated = timestamp;
    }

    public LeanChunkReassembler(LeanReassemblyBudget budget, TimeProvider? clock = null, Action? onIncompleteAbuse = null)
    {
        _budget = budget;
        _clock = clock ?? TimeProvider.System;
        _onIncompleteAbuse = onIncompleteAbuse;
        _expiry = _clock.CreateTimer(_ => Expire(), null, InactivityTimeout, InactivityTimeout);
    }

    public LeanProofWrapperMessage? Add(LeanProofChunkMessage chunk)
    {
        if (!LeanProofChunkMessage.IsValidGeometry(chunk.TotalBytes, chunk.Index, chunk.Count, chunk.ChunkSize, chunk.Data.Length))
            throw new RlpException("Invalid lean chunk geometry");
        lock (_lock)
        {
            if (_disposed) return null;
            if (ExpireCore()) throw new RlpException("Repeated incomplete lean streams");
            long now = _clock.GetTimestamp();
            if (_completed.Contains(chunk.WrapperHash)) return null;
            if (!_assemblies.TryGetValue(chunk.WrapperHash, out Assembly? assembly))
            {
                // Later fragments of a stream dropped under local pressure cannot open a hole.
                if (chunk.Index != 0 || _retained == MaxPeerAssemblies) return null;
                if (chunk.Count == 1) return CompleteSingleChunk(chunk);
                int metadataBytes = chunk.Count * 16;
                ValueHash256 hash = chunk.WrapperHash;
                LeanReassemblyBudget.Lease? lease = _budget.TryRent(metadataBytes, bytes => ReleasePeer(hash, bytes));
                if (lease is null) return null;
                _retained++;
                _retainedBytes += metadataBytes;
                try { assembly = new(chunk, lease, now); }
                catch { lease.Dispose(); throw; }
                _assemblies.Add(chunk.WrapperHash, assembly);
            }
            if (assembly.TotalBytes != chunk.TotalBytes || assembly.ChunkSize != chunk.ChunkSize)
            {
                Drop(chunk.WrapperHash, assembly);
                throw new RlpException("Inconsistent lean chunk geometry");
            }
            if (chunk.Index < assembly.Received)
            {
                if (!assembly.Chunks[chunk.Index].Span.SequenceEqual(chunk.Data.Span))
                {
                    Drop(chunk.WrapperHash, assembly);
                    throw new RlpException("Conflicting lean chunk");
                }
                return null;
            }
            if (chunk.Index != assembly.Received)
            {
                Drop(chunk.WrapperHash, assembly);
                throw new RlpException("Out-of-order lean chunk");
            }
            if (!TryGrow(assembly, chunk.Data.Length, finalFragment: chunk.Index == chunk.Count - 1))
            {
                Drop(chunk.WrapperHash, assembly);
                return null;
            }
            try
            {
                // A deserialized fragment owns its exact array; direct callers may provide a slice
                // backed by a much larger object, which must not escape the byte accounting.
                if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(chunk.Data, out ArraySegment<byte> segment)
                    && segment.Offset == 0 && segment.Count == segment.Array!.Length)
                    assembly.Chunks[chunk.Index] = chunk.Data;
                else assembly.Chunks[chunk.Index] = chunk.Data.ToArray();
            }
            catch { Drop(chunk.WrapperHash, assembly); throw; }
            assembly.ReceivedBytes += chunk.Data.Length;
            assembly.Updated = now;
            if (++assembly.Received != assembly.Chunks.Length) return null;
            if (!TryGrow(assembly, assembly.TotalBytes, completionCopy: true))
            {
                Drop(chunk.WrapperHash, assembly);
                return null;
            }
            byte[] bytes;
            try
            {
                bytes = new byte[assembly.TotalBytes];
                int offset = 0;
                foreach (ReadOnlyMemory<byte> fragment in assembly.Chunks)
                {
                    fragment.Span.CopyTo(bytes.AsSpan(offset));
                    offset += fragment.Length;
                }
            }
            catch { Drop(chunk.WrapperHash, assembly); throw; }
            _assemblies.Remove(chunk.WrapperHash);
            Array.Clear(assembly.Chunks);
            int releasedBytes = assembly.ReceivedBytes + assembly.MetadataBytes;
            assembly.Lease.Complete(releasedBytes);
            _retainedBytes -= releasedBytes;
            if (ValueKeccak.Compute(bytes) != chunk.WrapperHash)
            {
                assembly.Lease.Dispose();
                throw new RlpException("Lean wrapper hash mismatch");
            }
            _completed.Add(chunk.WrapperHash);
            return new(bytes, assembly.Lease);
        }
    }

    private LeanProofWrapperMessage? CompleteSingleChunk(LeanProofChunkMessage chunk)
    {
        int length = chunk.Data.Length;
        ValueHash256 hash = chunk.WrapperHash;
        if (length > MaxPeerBytes - _retainedBytes) return null;
        LeanReassemblyBudget.Lease? lease = _budget.TryRent(length, bytes => ReleasePeer(hash, bytes), incomplete: false);
        if (lease is null) return null;
        _retained++;
        _retainedBytes += length;
        byte[] bytes;
        try
        {
            bytes = chunk.Data.ToArray();
            if (ValueKeccak.Compute(bytes) != hash) throw new RlpException("Lean wrapper hash mismatch");
        }
        catch { lease.Dispose(); throw; }
        _completed.Add(hash);
        return new(bytes, lease);
    }

    private bool TryGrow(Assembly assembly, int bytes, bool completionCopy = false, bool finalFragment = false)
    {
        int peerLimit = completionCopy ? MaxPeerCompletionBytes : MaxPeerBytes;
        if (bytes > peerLimit - _retainedBytes || !assembly.Lease.TryGrow(bytes, completionCopy || finalFragment)) return false;
        _retainedBytes += bytes;
        return true;
    }

    private void Drop(ValueHash256 hash, Assembly assembly)
    {
        _assemblies.Remove(hash);
        Array.Clear(assembly.Chunks);
        assembly.Lease.Dispose();
    }

    private void ReleasePeer(ValueHash256 hash, int bytes)
    {
        lock (_lock) { _retained--; _retainedBytes -= bytes; _completed.Remove(hash); }
    }
    private void Expire()
    {
        bool abuse;
        lock (_lock) abuse = !_disposed && ExpireCore();
        if (abuse) _onIncompleteAbuse?.Invoke();
    }

    private bool ExpireCore()
    {
        long now = _clock.GetTimestamp();
        Span<ValueHash256> expired = stackalloc ValueHash256[MaxPeerAssemblies];
        int count = 0;
        foreach ((ValueHash256 hash, Assembly assembly) in _assemblies)
            if (_clock.GetElapsedTime(assembly.Updated, now) >= InactivityTimeout
                || _clock.GetElapsedTime(assembly.Created, now) >= MaxAssemblyLifetime) expired[count++] = hash;
        for (int i = 0; i < count; i++) Drop(expired[i], _assemblies[expired[i]]);
        _expiredStreams += count;
        // One stalled transfer can be an ordinary link failure. Repeated abandonment is abuse.
        return count > 0 && _expiredStreams >= 2;
    }

    internal void DropIncompleteForPressure()
    {
        lock (_lock)
        {
            foreach (Assembly assembly in _assemblies.Values) assembly.Lease.Dispose();
            _assemblies.Clear();
        }
    }

    public void Dispose()
    {
        _expiry.Dispose();
        lock (_lock)
        {
            _disposed = true;
            foreach (Assembly assembly in _assemblies.Values) assembly.Lease.Dispose();
            _assemblies.Clear();
            _completed.Clear();
        }
    }
}
