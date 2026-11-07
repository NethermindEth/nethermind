// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>EIP-8437 hash-based omission: a transaction encoding previously sent in full on the connection may be replaced by
/// <c>[encoding-hash]</c>, the Keccak-256 of the pooled transaction bytes without their RLP string prefix, when that is
/// smaller.</summary>
internal static class WrapperCompaction
{
    /// <summary>The encoded length of a reference <c>[encoding-hash]</c>.</summary>
    public const int ReferenceLength = 2 + ValueHash256.MemorySize;

    public enum Outcome
    {
        /// <summary>The wrapper is complete, either as sent or after resolving its references.</summary>
        Complete,
        /// <summary>A referenced encoding is not cached: the item is discarded and fetched again in full, without penalty.</summary>
        Missing,
        /// <summary>A malformed reference or a reconstruction beyond the announced length.</summary>
        Malformed
    }

    /// <summary>The pooled encodings of a complete wrapper's transactions with their encoding hashes, in order.</summary>
    public static (ValueHash256 Hash, byte[] Encoding)[] Encodings(ReadOnlySpan<byte> wrapper)
    {
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader transactions = outer.ReadList().ReadList();
        List<(ValueHash256, byte[])> encodings = [];
        while (transactions.HasMore)
        {
            ReadOnlySpan<byte> encoding = transactions.ReadBytes();
            encodings.Add((ValueKeccak.Compute(encoding), encoding.ToArray()));
        }
        return [.. encodings];
    }

    /// <summary>The number of transactions a wrapper carries, read from item headers; 0 when it is malformed.</summary>
    public static int CountTransactions(ReadOnlySpan<byte> wrapper)
    {
        try
        {
            Eth73RlpReader outer = new(wrapper);
            Eth73RlpReader transactions = outer.ReadList().ReadList();
            int count = 0;
            for (; transactions.HasMore; count++) transactions.ReadItem();
            return count;
        }
        catch (RlpException) { return 0; }
    }

    /// <summary>The encoding hashes of a complete wrapper's transactions, in order.</summary>
    public static ValueHash256[] EncodingHashes(ReadOnlySpan<byte> wrapper)
    {
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader transactions = outer.ReadList().ReadList();
        List<ValueHash256> hashes = [];
        while (transactions.HasMore) hashes.Add(ValueKeccak.Compute(transactions.ReadBytes()));
        return [.. hashes];
    }

    /// <summary>Replaces each transaction encoding <paramref name="delivered"/> holds, and whose reference is smaller, with its
    /// reference; returns the wrapper unchanged when nothing is replaced.</summary>
    public static byte[] Compact(byte[] wrapper, Func<ValueHash256, bool> delivered)
    {
        ValueHash256[] hashes = EncodingHashes(wrapper);
        return Compact(wrapper, Array.ConvertAll(hashes, hash => delivered(hash)), hashes);
    }

    /// <summary>Replaces each transaction encoding selected by <paramref name="replace"/>, whose reference is smaller, with its
    /// reference to <paramref name="hashes"/>; returns the wrapper unchanged when nothing is replaced.</summary>
    /// <param name="replace">Per transaction, in order, whether the recipient holds its encoding.</param>
    /// <param name="hashes">The encoding hashes of the wrapper's transactions, in order.</param>
    public static byte[] Compact(byte[] wrapper, ReadOnlySpan<bool> replace, ReadOnlySpan<ValueHash256> hashes)
    {
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader fields = outer.ReadList();
        Eth73RlpReader transactions = fields.ReadList();
        List<byte[]> items = [];
        bool replaced = false;
        for (int i = 0; transactions.HasMore; i++)
        {
            ReadOnlySpan<byte> item = transactions.ReadItem();
            if (item.Length > ReferenceLength && replace[i])
            {
                items.Add(Reference(hashes[i]));
                replaced = true;
            }
            else items.Add(item.ToArray());
        }
        if (!replaced) return wrapper;
        byte[] mode = fields.ReadItem().ToArray();
        byte[] content = fields.ReadItem().ToArray();
        return Eth73Rlp.EncodeList(Eth73Rlp.EncodeList([.. items]), mode, content);
    }

    /// <summary>The length <see cref="Compact(byte[], ReadOnlySpan{bool}, ReadOnlySpan{ValueHash256})"/> returns for the same
    /// <paramref name="replace"/>, read from item headers alone.</summary>
    public static int CompactLength(ReadOnlySpan<byte> wrapper, ReadOnlySpan<bool> replace)
    {
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader fields = outer.ReadList();
        Eth73RlpReader transactions = fields.ReadList();
        int content = 0;
        bool replaced = false;
        for (int i = 0; transactions.HasMore; i++)
        {
            int length = transactions.ReadItem().Length;
            bool reference = length > ReferenceLength && replace[i];
            replaced |= reference;
            content += reference ? ReferenceLength : length;
        }
        if (!replaced) return wrapper.Length;
        int rest = fields.ReadItem().Length;
        rest += fields.ReadItem().Length;
        return Eth73Rlp.LengthOfList(Eth73Rlp.LengthOfList(content) + rest);
    }

    public static byte[] Reference(in ValueHash256 hash) => Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(hash.Bytes));

    /// <summary>Whether a returned wrapper carries any reference, read from its transaction list alone.</summary>
    public static bool HasReferences(ReadOnlySpan<byte> wire)
    {
        try
        {
            Eth73RlpReader outer = new(wire);
            Eth73RlpReader transactions = outer.ReadList().ReadList();
            while (transactions.HasMore)
            {
                if (transactions.IsListNext) return true;
                transactions.ReadItem();
            }
        }
        // A malformed wrapper is rejected by its hash or canonical encoding check.
        catch (RlpException) { }
        return false;
    }

    /// <summary>Resolves the references of a returned wrapper from cached, validated encodings.</summary>
    /// <param name="wire">The wrapper as returned.</param>
    /// <param name="announcedLength">The announced length of the complete wrapper, which reconstruction must not exceed.</param>
    /// <param name="lookup">The cached encoding with the given encoding hash, if any.</param>
    /// <param name="wrapper">The complete wrapper when <see cref="Outcome.Complete"/>.</param>
    public static Outcome Reconstruct(byte[] wire, int announcedLength, Func<ValueHash256, byte[]?> lookup, out byte[]? wrapper, out string? error)
    {
        wrapper = null;
        error = null;
        try
        {
            if (wire.Length > announcedLength) return Fail("a compact wrapper exceeds its announced length", out error);
            Eth73RlpReader outer = new(wire);
            Eth73RlpReader fields = outer.ReadList();
            outer.End();
            Eth73RlpReader transactions = fields.ReadList();
            List<byte[]> items = [];
            bool missing = false;
            long content = 0;
            while (transactions.HasMore)
            {
                if (!transactions.IsListNext)
                {
                    byte[] item = transactions.ReadItem().ToArray();
                    content += item.Length;
                    items.Add(item);
                    continue;
                }
                Eth73RlpReader reference = transactions.ReadList();
                ValueHash256 hash = reference.ReadHash();
                reference.End();
                if (lookup(hash) is not { } encoding)
                {
                    missing = true;
                    continue;
                }
                // Cache entries are keyed by their own hash, so this guards only against a corrupted cache.
                if (ValueKeccak.Compute(encoding) != hash) return Fail("a cached encoding does not match its hash", out error);
                byte[] full = Eth73Rlp.EncodeBytes(encoding);
                if (full.Length <= ReferenceLength) return Fail("a reference is not smaller than the encoding it replaces", out error);
                content += full.Length;
                if (content > announcedLength) return Fail("a reconstructed wrapper exceeds its announced length", out error);
                items.Add(full);
            }
            if (missing) return Outcome.Missing;
            byte[] mode = fields.ReadItem().ToArray();
            byte[] proof = fields.ReadItem().ToArray();
            fields.End();
            long length = Eth73Rlp.LengthOfList(Eth73Rlp.LengthOfList((int)content) + mode.Length + proof.Length);
            if (length > announcedLength || length > Eth73Protocol.MaxWrapperBytes)
                return Fail("a reconstructed wrapper exceeds its announced length", out error);
            wrapper = Eth73Rlp.EncodeList(Eth73Rlp.EncodeList([.. items]), mode, proof);
            return Outcome.Complete;
        }
        catch (RlpException exception)
        {
            return Fail($"malformed compact wrapper: {exception.Message}", out error);
        }

        static Outcome Fail(string reason, out string? error)
        {
            error = reason;
            return Outcome.Malformed;
        }
    }
}

/// <summary>Bytes held across every peer's <see cref="WrapperEncodingCache"/>.</summary>
internal sealed class WrapperEncodingBudget(long maxBytes)
{
    private long _used;

    public long Used => Interlocked.Read(ref _used);

    public bool TryTake(long bytes)
    {
        long used = Interlocked.Add(ref _used, bytes);
        if (used <= maxBytes) return true;
        Interlocked.Add(ref _used, -bytes);
        return false;
    }

    public void Release(long bytes) => Interlocked.Add(ref _used, -bytes);
}

/// <summary>Transaction encodings received in full within validated wrappers from one peer, keyed by encoding hash, from which
/// that peer's hash references are resolved; LRU, bounded per peer and by a budget shared across peers.</summary>
internal sealed class WrapperEncodingCache(WrapperEncodingBudget budget, long maxBytes = WrapperTransportLimits.MaxCachedEncodingBytesPerPeer)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, LinkedListNode<(ValueHash256 Hash, byte[] Encoding)>> _items = [];
    private readonly LinkedList<(ValueHash256 Hash, byte[] Encoding)> _order = new();
    private long _bytes;
    private bool _closed;

    public bool IsEmpty { get { lock (_lock) return _items.Count == 0; } }
    public long Bytes { get { lock (_lock) return _bytes; } }

    public void Add(in ValueHash256 hash, byte[] encoding)
    {
        long size = Charge(encoding);
        lock (_lock)
        {
            if (_closed || size > maxBytes) return;
            if (_items.TryGetValue(hash, out LinkedListNode<(ValueHash256, byte[])>? existing))
            {
                _order.Remove(existing);
                _order.AddLast(existing);
                return;
            }
            while (_bytes + size > maxBytes) Evict();
            // Under pressure from other peers, this peer gives up its own oldest entries first.
            while (!budget.TryTake(size))
            {
                if (_items.Count == 0) return;
                Evict();
            }
            _items[hash] = _order.AddLast((hash, encoding));
            _bytes += size;
        }
    }

    public byte[]? Get(in ValueHash256 hash)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(hash, out LinkedListNode<(ValueHash256 Hash, byte[] Encoding)>? node)) return null;
            _order.Remove(node);
            _order.AddLast(node);
            return node.Value.Encoding;
        }
    }

    /// <summary>Releases every entry when the connection closes.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            while (_items.Count > 0) Evict();
        }
    }

    private void Evict()
    {
        (ValueHash256 hash, byte[] encoding) = _order.First!.Value;
        _order.RemoveFirst();
        _items.Remove(hash);
        long size = Charge(encoding);
        _bytes -= size;
        budget.Release(size);
    }

    private static long Charge(byte[] encoding) => encoding.Length + 96L;
}
