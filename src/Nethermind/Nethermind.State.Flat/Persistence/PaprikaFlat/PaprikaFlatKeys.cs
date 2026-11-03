// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Paprika.Data;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>The Paprika keys of <see cref="Db.FlatLayout.PaprikaFlat"/>'s accounts and slots.</summary>
/// <remarks>
/// As in the other flat layouts, an account is keyed by the first <see cref="AccountKeyLength"/> bytes of its path, so
/// paths sharing them share one account and one storage. An account key is those bytes as a 40-nibble state path, and
/// storage is keyed by them padded to the 32 bytes Paprika reads a contract key as. Every other state key is shorter than
/// two nibbles, as <see cref="PaprikaFlatMetadata.BlobKey"/> is: a range delete drops every state key under a prefix of
/// two or more nibbles.
/// </remarks>
internal static class PaprikaFlatKeys
{
    /// <summary>The bytes of an account path that key its account and storage.</summary>
    public const int AccountKeyLength = BaseFlatPersistence.AccountKeyLength;

    /// <summary>The Paprika key of the account at <paramref name="accountPath"/>.</summary>
    /// <param name="accountPath">The account path, or at least its first <see cref="AccountKeyLength"/> bytes.</param>
    public static Key Account(ReadOnlySpan<byte> accountPath) => Key.Account(NibblePath.FromKey(accountPath[..AccountKeyLength]));

    /// <summary>
    /// The 32-byte contract key of the storage of the account at <paramref name="accountPath"/>: its first
    /// <see cref="AccountKeyLength"/> bytes followed by zeros.
    /// </summary>
    /// <remarks>
    /// Paprika reads a contract key as a whole 32-byte Keccak, so a storage key is never built over the shorter account
    /// key. It is also the account iterator's key, the account key zero-padded as the other flat layouts return it.
    /// </remarks>
    /// <param name="accountPath">The account path, or at least its first <see cref="AccountKeyLength"/> bytes.</param>
    public static ValueHash256 StorageAccount(ReadOnlySpan<byte> accountPath)
    {
        ValueHash256 storageAccount = default;
        accountPath[..AccountKeyLength].CopyTo(storageAccount.BytesAsSpan);
        return storageAccount;
    }

    /// <summary>The Paprika key of <paramref name="slot"/> in the storage of <paramref name="storageAccount"/>.</summary>
    /// <param name="storageAccount">The 32-byte contract key, see <see cref="StorageAccount"/>.</param>
    /// <param name="slot">The 32-byte slot hash.</param>
    public static Key Slot(ReadOnlySpan<byte> storageAccount, ReadOnlySpan<byte> slot) =>
        Key.StorageCell(NibblePath.FromKey(storageAccount), slot);

    /// <summary>The prefix of every slot in the storage of <paramref name="storageAccount"/>.</summary>
    /// <param name="storageAccount">The 32-byte contract key, see <see cref="StorageAccount"/>.</param>
    public static Key Storage(ReadOnlySpan<byte> storageAccount) =>
        Key.StorageCell(NibblePath.FromKey(storageAccount), NibblePath.Empty);
}
