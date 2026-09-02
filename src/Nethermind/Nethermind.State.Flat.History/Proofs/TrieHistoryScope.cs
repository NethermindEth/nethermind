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

    public abstract bool HasCommitmentRows(int depth);

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
        return commitments.OpenAtOrBelow(prefix[..prefixLength], suffix, budget);
    }

    public void EnumerateLeaves(in TreePath prefix, ulong block, ResolutionBudget budget, List<TrieLeaf> leaves)
    {
        Span<byte> lower = stackalloc byte[MaxRowKeyLength];
        Span<byte> upper = stackalloc byte[MaxRowKeyLength];
        int boundLength = WriteScopedBounds(prefix, lower, upper);

        using ISortedView view = rows.GetViewBetween(lower[..boundLength], upper[..boundLength], ReadFlags.HintCacheMiss);

        ValueHash256 currentPath = default;
        bool haveGroup = false;
        bool resolved = false;

        while (view.MoveNext())
        {
            budget.ChargeRow();

            ReadOnlySpan<byte> rowKey = view.CurrentKey;
            if (rowKey.Length != RowKeyLength || !BelongsToScope(rowKey)) continue;

            ReadOnlySpan<byte> pathBytes = rowKey.Slice(TriePathOffset, Hash256.Size);
            if (!haveGroup || !pathBytes.SequenceEqual(currentPath.Bytes))
            {
                currentPath = new ValueHash256(pathBytes);
                haveGroup = true;
                resolved = false;
            }
            else if (resolved)
            {
                continue;
            }

            ulong rowBlock = rowFormat.DecodeSuffixBlock(rowKey[^sizeof(ulong)..]);
            if (rowBlock > block) continue;

            resolved = true;

            ReadOnlySpan<byte> storedValue = view.CurrentValue;
            if (storedValue.IsEmpty || !SurvivesTo(currentPath, rowBlock, block)) continue;

            byte[]? leafValue = DecodeLeafValue(storedValue);
            if (leafValue is null) continue;

            leaves.Add(new TrieLeaf(currentPath, leafValue));
        }
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
