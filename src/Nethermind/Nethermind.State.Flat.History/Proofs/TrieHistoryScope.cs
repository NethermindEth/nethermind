// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

internal abstract class TrieHistoryScope(
    ISortedKeyValueStore rows,
    HistoryRowFormat rowFormat,
    CommitmentStore commitments,
    CommitmentDepthPolicy policy)
{
    public const int MaxRowKeyLength = BaseFlatPersistence.StorageKeyLength + sizeof(ulong);

    public CommitmentDepthPolicy Policy => policy;

    public ulong MinEpoch { get; init; }

    public Func<ulong>? MinEpochSource { get; init; }

    public Func<ulong>? FineMinEpochSource { get; init; }

    protected virtual ulong? ProbeStartEpoch => null;

    public virtual void NoteRootLastBlock(ulong block)
    {
    }

    public abstract bool HasCommitmentRows(int depth);

    public virtual bool IsComposed(int depth) => false;

    public abstract bool MayHaveExactRows(int depth);

    public abstract int WriteCommitmentPrefix(Span<byte> destination, in TreePath path, bool exact);

    protected abstract int RowKeyLength { get; }

    protected abstract int TriePathOffset { get; }

    protected abstract bool BelongsToScope(scoped ReadOnlySpan<byte> rowKey);

    protected abstract int WriteScopedBounds(in TreePath prefix, Span<byte> lower, Span<byte> upper);

    protected abstract byte[]? DecodeLeafValue(scoped ReadOnlySpan<byte> storedValue);

    protected virtual bool SurvivesTo(in ValueHash256 triePath, ulong writtenAtBlock, ulong block) => true;

    public CommitmentStore.RowChain OpenRows(in TreePath path, bool exact, ulong suffix, ResolutionBudget? budget = null, bool bounded = false)
    {
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = WriteCommitmentPrefix(prefix, path, exact);
        ulong minEpoch = MinEpochSource?.Invoke() ?? MinEpoch;
        if (exact && FineMinEpochSource is not null) minEpoch = Math.Max(minEpoch, FineMinEpochSource());
        return commitments.OpenAtOrBelow(prefix[..prefixLength], suffix, budget, minEpoch, ProbeStartEpoch, bounded);
    }

    public void EnumerateLeaves(in TreePath prefix, ulong block, ResolutionBudget budget, List<TrieLeaf> leaves)
    {
        Span<byte> lower = stackalloc byte[MaxRowKeyLength];
        Span<byte> upper = stackalloc byte[MaxRowKeyLength + 1];
        int boundLength = WriteScopedBounds(prefix, lower, upper);
        upper[boundLength] = 0x00;
        ReadOnlySpan<byte> bound = upper[..(boundLength + 1)];

        int keyLength = RowKeyLength - sizeof(ulong);
        Span<byte> cursor = stackalloc byte[MaxRowKeyLength + 1];
        lower[..boundLength].CopyTo(cursor);
        int cursorLength = boundLength;
        Span<byte> found = stackalloc byte[MaxRowKeyLength];
        Span<byte> value = stackalloc byte[LeafValueBuffer];
        Span<byte> owner = stackalloc byte[MaxRowKeyLength];

        bool haveRow = false;
        int foundLength = 0;
        int valueLength = 0;
        while (true)
        {
            if (!haveRow)
            {
                if (!SeekRow(cursor[..cursorLength], bound, budget, found, out foundLength, value, out valueLength)) return;
            }

            haveRow = false;
            if (foundLength != RowKeyLength)
            {
                cursorLength = Advance(cursor, found[..foundLength]);
                continue;
            }

            ReadOnlySpan<byte> keyPart = found[..keyLength];
            if (!BelongsToScope(found[..foundLength]))
            {
                cursorLength = SkipKey(cursor, keyPart);
                continue;
            }

            ulong rowBlock = rowFormat.DecodeSuffixBlock(found[keyLength..foundLength]);
            if (rowBlock <= block)
            {
                Collect(keyPart, rowBlock, value[..valueLength], block, leaves);
                cursorLength = SkipKey(cursor, keyPart);
                continue;
            }

            Span<byte> seek = cursor[..RowKeyLength];
            keyPart.CopyTo(seek);
            rowFormat.EncodeSuffixBlock(seek[keyLength..], block);
            keyPart.CopyTo(owner);
            if (!SeekRow(seek, bound, budget, found, out foundLength, value, out valueLength)) return;

            if (foundLength == RowKeyLength && found[..keyLength].SequenceEqual(owner[..keyLength]))
            {
                Collect(owner[..keyLength], rowFormat.DecodeSuffixBlock(found[keyLength..foundLength]), value[..valueLength], block, leaves);
                cursorLength = SkipKey(cursor, owner[..keyLength]);
                continue;
            }

            haveRow = true;
        }
    }

    private const int LeafValueBuffer = 512;

    private bool SeekRow(ReadOnlySpan<byte> from, ReadOnlySpan<byte> bound, ResolutionBudget budget, Span<byte> key, out int keyLength, Span<byte> value, out int valueLength)
    {
        if (!rows.TryGetCeiling(from, bound, key, out keyLength, value, out valueLength)) return false;

        budget.ChargeRow();
        if (valueLength <= value.Length) return true;

        using ISortedView view = rows.GetViewBetween(from, bound);
        if (!view.MoveNext()) return false;

        ReadOnlySpan<byte> stored = view.CurrentValue;
        valueLength = stored.Length;
        if (valueLength > value.Length) throw new StateUnavailableException($"A history row value of {valueLength} bytes exceeds the {value.Length} bytes a leaf can carry.");

        stored.CopyTo(value);
        return true;
    }

    private void Collect(scoped ReadOnlySpan<byte> keyPart, ulong rowBlock, scoped ReadOnlySpan<byte> storedValue, ulong block, List<TrieLeaf> leaves)
    {
        ValueHash256 triePath = new(keyPart.Slice(TriePathOffset, Hash256.Size));
        if (storedValue.IsEmpty || !SurvivesTo(triePath, rowBlock, block)) return;

        byte[]? leafValue = DecodeLeafValue(storedValue);
        if (leafValue is null) return;

        leaves.Add(new TrieLeaf(triePath, leafValue));
    }

    private static int SkipKey(Span<byte> cursor, scoped ReadOnlySpan<byte> keyPart)
    {
        keyPart.CopyTo(cursor);
        cursor.Slice(keyPart.Length, sizeof(ulong)).Fill(0xFF);
        cursor[keyPart.Length + sizeof(ulong)] = 0x00;
        return keyPart.Length + sizeof(ulong) + 1;
    }

    private static int Advance(Span<byte> cursor, scoped ReadOnlySpan<byte> rowKey)
    {
        rowKey.CopyTo(cursor);
        cursor[rowKey.Length] = 0x00;
        return rowKey.Length + 1;
    }

    protected static int WritePathBounds(in TreePath prefix, Span<byte> lower, Span<byte> upper, int offset)
    {
        int wholeBytes = prefix.Length / 2;
        prefix.Path.Bytes[..wholeBytes].CopyTo(lower[offset..]);
        prefix.Path.Bytes[..wholeBytes].CopyTo(upper[offset..]);

        int written = wholeBytes;
        if ((prefix.Length & 1) == 1)
        {
            byte half = (byte)(prefix.Path.Bytes[wholeBytes] & 0xF0);
            lower[offset + wholeBytes] = half;
            upper[offset + wholeBytes] = (byte)(half | 0x0F);
            written++;
        }

        lower.Slice(offset + written, Hash256.Size - written).Clear();
        upper.Slice(offset + written, Hash256.Size - written).Fill(0xFF);
        return offset + Hash256.Size;
    }
}
