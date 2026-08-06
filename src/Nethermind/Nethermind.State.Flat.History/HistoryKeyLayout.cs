// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// The single owner of "how long is this column's flat key, and how does its 20-byte account key sit inside it" —
/// resolved once per column and shared by every collaborator that needs to reassemble the scope-lookup key from a
/// raw <c>AccountHistory</c>/<c>StorageHistory</c> flat key, instead of each one independently branching on which
/// column it is. <see cref="Account"/>'s key already is the account key; <see cref="Storage"/>'s splits it as
/// <c>[4B prefix | 32B slot | 16B suffix]</c> (see <see cref="BaseFlatPersistence"/>'s remarks), so extraction
/// reassembles the leading 4 and trailing 16 bytes.
/// </summary>
public sealed class HistoryKeyLayout
{
    public static readonly HistoryKeyLayout Account = new(BaseFlatPersistence.AccountKeyLength, isStorage: false);
    public static readonly HistoryKeyLayout Storage = new(BaseFlatPersistence.StorageKeyLength, isStorage: true);

    private readonly bool _isStorage;

    private HistoryKeyLayout(int flatKeyLength, bool isStorage)
    {
        FlatKeyLength = flatKeyLength;
        _isStorage = isStorage;
    }

    public int FlatKeyLength { get; }

    public void ExtractAddressKey(ReadOnlySpan<byte> flatKey, Span<byte> addressKey)
    {
        if (!_isStorage)
        {
            flatKey.CopyTo(addressKey);
            return;
        }

        const int prefixLength = BasePersistence.StoragePrefixPortion;
        flatKey[..prefixLength].CopyTo(addressKey);
        flatKey[^(BaseFlatPersistence.AccountKeyLength - prefixLength)..].CopyTo(addressKey[prefixLength..]);
    }
}
