// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// The single owner of "how long is this column's flat key, and how does its scope key sit inside it" — resolved
/// once per column and shared by every collaborator that needs to reassemble the scope-lookup key from a raw
/// <c>AccountHistory</c>/<c>StorageHistory</c> flat key, instead of each one independently branching on which
/// column it is. <see cref="Account"/>'s key is the whole 32-byte trie path, whose leading
/// <see cref="ScopeKeyLength"/> bytes are the scope key; <see cref="Storage"/>'s key carries that same leading
/// portion split as <c>[4B prefix | 32B slot | 16B suffix]</c> (see <see cref="BaseFlatPersistence"/>'s remarks),
/// so extraction reassembles the leading 4 and trailing 16 bytes.
/// </summary>
public sealed class HistoryKeyLayout
{
    public const int AccountKeyLength = Hash256.Size;

    public const int ScopeKeyLength = BaseFlatPersistence.AccountKeyLength;

    public static readonly HistoryKeyLayout Account = new(AccountKeyLength, isStorage: false);
    public static readonly HistoryKeyLayout Storage = new(BaseFlatPersistence.StorageKeyLength, isStorage: true);

    private readonly bool _isStorage;

    private HistoryKeyLayout(int flatKeyLength, bool isStorage)
    {
        FlatKeyLength = flatKeyLength;
        _isStorage = isStorage;
    }

    public int FlatKeyLength { get; }

    public static ReadOnlySpan<byte> EncodeAccountKey(Span<byte> buffer, in ValueHash256 accountPath)
    {
        accountPath.Bytes.CopyTo(buffer);
        return buffer[..AccountKeyLength];
    }

    /// <summary>Narrows a history account key to the live flat State column's own truncated key, for the v3 read
    /// paths that fall through to it. Exact in this direction only: the flat key is a prefix of the same account
    /// path, so widening back is a preimage search, not an encoding.</summary>
    public static ReadOnlySpan<byte> ToFlatStateKey(ReadOnlySpan<byte> accountKey) =>
        accountKey[..BaseFlatPersistence.AccountKeyLength];

    public void ExtractAddressKey(ReadOnlySpan<byte> flatKey, Span<byte> addressKey)
    {
        if (!_isStorage)
        {
            flatKey[..ScopeKeyLength].CopyTo(addressKey);
            return;
        }

        const int prefixLength = BasePersistence.StoragePrefixPortion;
        flatKey[..prefixLength].CopyTo(addressKey);
        flatKey[^(ScopeKeyLength - prefixLength)..].CopyTo(addressKey[prefixLength..]);
    }
}
