// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>Per-column key shape. <see cref="Account"/> is the whole 32-byte trie path; <see cref="Storage"/>
/// splits the same leading portion as <c>[4B prefix | 32B slot | 16B suffix]</c>.</summary>
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

    /// <summary>Narrows to the live State column's truncated key. This direction only - widening back would be a
    /// preimage search.</summary>
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
