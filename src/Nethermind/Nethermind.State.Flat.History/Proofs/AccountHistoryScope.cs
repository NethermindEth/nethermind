// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class AccountHistoryScope(
    ISortedKeyValueStore rows,
    HistoryRowFormat rowFormat,
    CommitmentStore commitments,
    CommitmentDepthPolicy policy)
    : TrieHistoryScope(rows, rowFormat, commitments, policy)
{
    private static readonly AccountDecoder Decoder = new();

    public override bool HasCommitmentRows(int depth) => depth <= Policy.AccountCheckpointDepth && !Policy.IsComposedAccountDepth(depth);

    public override bool IsComposed(int depth) => Policy.IsComposedAccountDepth(depth);

    public override bool MayHaveExactRows(int depth) => Policy.IsExactAccountDepth(depth);

    public override int WriteCommitmentPrefix(Span<byte> destination, in TreePath path, bool exact) =>
        CommitmentKeyLayout.WritePathPrefix(destination, path, exact);

    protected override int RowKeyLength => Hash256.Size + sizeof(ulong);

    protected override int TriePathOffset => 0;

    protected override bool BelongsToScope(scoped ReadOnlySpan<byte> rowKey) => true;

    protected override int WriteScopedBounds(in TreePath prefix, Span<byte> lower, Span<byte> upper)
    {
        int written = WritePathBounds(prefix, lower, upper, 0);
        lower.Slice(written, sizeof(ulong)).Clear();
        upper.Slice(written, sizeof(ulong)).Fill(0xFF);
        return written + sizeof(ulong);
    }

    protected override byte[]? DecodeLeafValue(scoped ReadOnlySpan<byte> storedValue)
    {
        try
        {
            return AccountRowRlp.Encode(storedValue);
        }
        catch (InvalidDataException e)
        {
            throw new StateUnavailableException(e.Message);
        }
    }
}
