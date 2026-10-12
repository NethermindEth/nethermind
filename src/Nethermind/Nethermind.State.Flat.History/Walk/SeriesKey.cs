// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal readonly struct SeriesKey(bool isStorage, in ValueHash256 identity, in TreePath path, bool scratch)
{
    public const byte ScratchMarker = 0xFD;
    public const int MaxPrefixLength = 2 + CommitmentKeyLayout.IdentityLength + CommitmentKeyLayout.MaxPathLength;
    public const int MaxKeyLength = MaxPrefixLength + sizeof(ulong) + 1;

    private readonly ValueHash256 _identity = identity;
    private readonly TreePath _path = path;

    public bool Scratch => scratch;

    public FlatHistoryColumns Column => scratch || !isStorage ? FlatHistoryColumns.AccountCommitments : FlatHistoryColumns.StorageCommitments;

    public int WritePrefix(Span<byte> destination)
    {
        if (!scratch)
        {
            if (!isStorage) return CommitmentKeyLayout.WritePathPrefix(destination, _path, exact: true);

            Span<byte> identityBytes = stackalloc byte[CommitmentKeyLayout.IdentityLength];
            CommitmentKeyLayout.WriteIdentity(identityBytes, _identity);
            return CommitmentKeyLayout.WriteScopedPathPrefix(destination, identityBytes, _path, exact: true);
        }

        destination[0] = ScratchMarker;
        destination[1] = isStorage ? (byte)1 : (byte)0;
        int written = 2;
        if (isStorage)
        {
            CommitmentKeyLayout.WriteIdentity(destination[written..], _identity);
            written += CommitmentKeyLayout.IdentityLength;
        }

        return written + CommitmentKeyLayout.WritePathPrefix(destination[written..], _path, exact: false);
    }
}
