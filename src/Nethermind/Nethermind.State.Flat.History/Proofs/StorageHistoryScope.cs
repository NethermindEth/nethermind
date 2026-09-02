// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class StorageHistoryScope(
    ISortedKeyValueStore rows,
    HistoryRowFormat rowFormat,
    CommitmentStore commitments,
    CommitmentDepthPolicy policy,
    StorageClearStore clears,
    ValueHash256 accountPath,
    bool rlpWrapSlots)
    : TrieHistoryScope(rows, rowFormat, commitments, policy)
{
    private const int IdentityPrefixLength = BasePersistence.StoragePrefixPortion;
    private const int SlotPathOffset = IdentityPrefixLength;
    private const int IdentitySuffixOffset = SlotPathOffset + Hash256.Size;
    private const int IdentitySuffixLength = CommitmentKeyLayout.IdentityLength - IdentityPrefixLength;

    private readonly byte[] _identity = accountPath.Bytes[..CommitmentKeyLayout.IdentityLength].ToArray();

    public override CommitmentTier TierOf(int depth) => Policy.StorageTier(depth, largeTrie: false);

    public override bool MayHaveExactRows(int depth) => depth <= Policy.StorageExactDepth;

    public override int WriteCommitmentPrefix(Span<byte> destination, in TreePath path, bool exact) =>
        CommitmentKeyLayout.WriteScopedPathPrefix(destination, _identity, path, exact);

    protected override int RowKeyLength => BaseFlatPersistence.StorageKeyLength + sizeof(ulong);

    protected override int TriePathOffset => SlotPathOffset;

    protected override bool BelongsToScope(scoped ReadOnlySpan<byte> rowKey) =>
        rowKey.Slice(IdentitySuffixOffset, IdentitySuffixLength).SequenceEqual(_identity.AsSpan(IdentityPrefixLength));

    protected override int WriteScopedBounds(in TreePath prefix, Span<byte> lower, Span<byte> upper)
    {
        _identity.AsSpan(0, IdentityPrefixLength).CopyTo(lower);
        _identity.AsSpan(0, IdentityPrefixLength).CopyTo(upper);

        int written = WritePathBounds(prefix, lower, upper, IdentityPrefixLength);

        lower.Slice(written, IdentitySuffixLength + sizeof(ulong)).Clear();
        upper.Slice(written, IdentitySuffixLength + sizeof(ulong)).Fill(0xFF);
        return written + IdentitySuffixLength + sizeof(ulong);
    }

    protected override bool SurvivesTo(in ValueHash256 triePath, ulong writtenAtBlock, ulong block) =>
        !clears.HasClearInRange(accountPath.Bytes, writtenAtBlock, block);

    protected override byte[]? DecodeLeafValue(scoped ReadOnlySpan<byte> storedValue)
    {
        if (!rlpWrapSlots)
        {
            ReadOnlySpan<byte> stripped = storedValue.WithoutLeadingZeros();
            return stripped.IsEmpty ? null : Rlp.Encode(stripped).Bytes;
        }

        RlpReader reader = new(storedValue);
        return reader.DecodeByteArraySpan().WithoutLeadingZeros().IsEmpty ? null : storedValue.ToArray();
    }
}
