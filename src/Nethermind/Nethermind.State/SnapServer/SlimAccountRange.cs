// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.State.SnapServer;

/// <summary>Accounts of a served account range, each kept as its slim RLP in one pooled buffer that the reply owns.</summary>
public sealed class SlimAccountRange : IOwnedReadOnlyList<PathWithAccount>
{
    private const int MaxSlimAccountLength = 2 + 9 + 33 + 2 * Rlp.LengthOfKeccakRlp;

    private readonly ArrayPoolList<byte> _rlp;
    private readonly ArrayPoolList<Entry> _entries = new(0);
    private PathWithAccount[]? _accounts;

    public SlimAccountRange() : this(SafeArrayPool<byte>.Shared) { }

    internal SlimAccountRange(ArrayPool<byte> pool) => _rlp = new ArrayPoolList<byte>(pool, 0);

    public int Count => _entries.Count;

    /// <summary>The RLP length of all the path and account pairs, each with its sequence prefix.</summary>
    public int EntriesLength { get; private set; }

    public PathWithAccount this[int index] => Materialize()[index];

    /// <summary>The path of the account at <paramref name="index"/>.</summary>
    public ref readonly ValueHash256 GetPath(int index) => ref _entries.AsSpan()[index].Path;

    /// <summary>The slim RLP of the account at <paramref name="index"/>.</summary>
    public ReadOnlySpan<byte> GetAccountRlp(int index)
    {
        Entry entry = _entries[index];
        return _rlp.AsSpan().Slice(entry.Start, entry.Length);
    }

    [SkipLocalsInit]
    internal int AddLeaf(in ValueHash256 path, ReadOnlySpan<byte> leafValue)
    {
        Span<byte> buffer = stackalloc byte[MaxSlimAccountLength];
        int length = leafValue.IsEmpty ? WriteNull(buffer) : WriteSlim(leafValue, buffer);
        _entries.Add(new Entry(path, _rlp.Count, length));
        _rlp.AddRange(buffer[..length]);
        EntriesLength += Rlp.LengthOfSequence(Rlp.LengthOfKeccakRlp + length);
        return length;
    }

    private static int WriteNull(Span<byte> buffer)
    {
        buffer[0] = Rlp.EmptyListByte;
        return 1;
    }

    private static int WriteSlim(ReadOnlySpan<byte> leafValue, Span<byte> buffer)
    {
        RlpReader reader = new(leafValue);
        if (!AccountDecoder.Instance.TryDecodeStruct<OffFlag>(ref reader, out AccountStruct account))
        {
            throw new TrieException("An account leaf must contain a decodable account value.");
        }

        int contentLength = Rlp.LengthOf(account.Nonce)
            + Rlp.LengthOf(account.Balance)
            + (account.HasStorage ? Rlp.LengthOfKeccakRlp : 1)
            + (account.HasCode ? Rlp.LengthOfKeccakRlp : 1);

        RlpWriter writer = RlpWriter.ContentWriter(buffer);
        RlpSequence sequence = writer.StartSequence(contentLength);
        writer.Encode(account.Nonce);
        writer.Encode(account.Balance);
        if (account.HasStorage)
        {
            writer.Encode(account.StorageRoot);
        }
        else
        {
            writer.EncodeEmptyByteArray();
        }

        if (account.HasCode)
        {
            writer.Encode(account.CodeHash);
        }
        else
        {
            writer.EncodeEmptyByteArray();
        }

        writer.EndSequence(sequence);
        return writer.Length;
    }

    public ReadOnlySpan<PathWithAccount> AsSpan() => Materialize();

    private PathWithAccount[] Materialize()
    {
        if (_accounts is not null) return _accounts;

        PathWithAccount[] accounts = new PathWithAccount[_entries.Count];
        for (int i = 0; i < accounts.Length; i++)
        {
            ReadOnlySpan<byte> rlp = GetAccountRlp(i);
            Account? account = rlp is [Rlp.EmptyListByte] ? null : AccountDecoder.Slim.Decode(rlp);
            accounts[i] = new PathWithAccount(GetPath(i), account);
        }

        return _accounts = accounts;
    }

    public IEnumerator<PathWithAccount> GetEnumerator() => ((IEnumerable<PathWithAccount>)Materialize()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        _rlp.Dispose();
        _entries.Dispose();
    }

    private readonly struct Entry(in ValueHash256 path, int start, int length)
    {
        public readonly ValueHash256 Path = path;
        public readonly int Start = start;
        public readonly int Length = length;
    }
}
