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

    public CommitmentStore.RowChain OpenRows(in TreePath path, bool exact, ulong suffix, ResolutionBudget? budget = null)
    {
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = WriteCommitmentPrefix(prefix, path, exact);
        return commitments.OpenAtOrBelow(prefix[..prefixLength], suffix, budget, MinEpoch, ProbeStartEpoch);
    }

    public void EnumerateLeaves(in TreePath prefix, ulong block, ResolutionBudget budget, List<TrieLeaf> leaves)
    {
        Span<byte> lower = stackalloc byte[MaxRowKeyLength];
        Span<byte> upper = stackalloc byte[MaxRowKeyLength + 1];
        int boundLength = WriteScopedBounds(prefix, lower, upper);
        upper[boundLength] = 0x00;
        int upperLength = boundLength + 1;

        int keyLength = RowKeyLength - sizeof(ulong);
        Span<byte> cursor = stackalloc byte[MaxRowKeyLength + 1];
        lower[..boundLength].CopyTo(cursor);
        int cursorLength = boundLength;
        Span<byte> seek = stackalloc byte[MaxRowKeyLength];

        while (true)
        {
            using (ISortedView view = rows.GetViewBetween(cursor[..cursorLength], upper[..upperLength], ReadFlags.HintCacheMiss))
            {
                if (!view.MoveNext()) return;

                budget.ChargeRow();
                ReadOnlySpan<byte> rowKey = view.CurrentKey;
                if (rowKey.Length != RowKeyLength)
                {
                    cursorLength = Advance(cursor, rowKey);
                    continue;
                }

                rowKey[..keyLength].CopyTo(seek);
                ulong rowBlock = rowFormat.DecodeSuffixBlock(rowKey[keyLength..]);
                ReadOnlySpan<byte> storedValue = view.CurrentValue;
                if (rowBlock <= block && BelongsToScope(rowKey))
                {
                    Collect(seek[..keyLength], rowBlock, storedValue, block, leaves);
                    cursorLength = SkipKey(cursor, seek[..keyLength]);
                    continue;
                }
            }

            if (!BelongsToScope(seek[..keyLength]))
            {
                cursorLength = SkipKey(cursor, seek[..keyLength]);
                continue;
            }

            rowFormat.EncodeSuffixBlock(seek[keyLength..], block);
            using (ISortedView atBlock = rows.GetViewBetween(seek[..RowKeyLength], upper[..upperLength], ReadFlags.HintCacheMiss))
            {
                if (!atBlock.MoveNext()) return;

                budget.ChargeRow();
                ReadOnlySpan<byte> found = atBlock.CurrentKey;
                if (found.Length == RowKeyLength && found[..keyLength].SequenceEqual(seek[..keyLength]))
                {
                    Collect(seek[..keyLength], rowFormat.DecodeSuffixBlock(found[keyLength..]), atBlock.CurrentValue, block, leaves);
                }
            }

            cursorLength = SkipKey(cursor, seek[..keyLength]);
        }
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
