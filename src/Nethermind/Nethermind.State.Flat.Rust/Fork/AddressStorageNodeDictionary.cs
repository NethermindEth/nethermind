// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>Not a dictionary: only the holder of <see cref="AddressNodes"/>.</summary>
/// <remarks>
/// The storage trie nodes of a <see cref="SnapshotBundle"/> are written into its native scope. The type has the name of
/// the C# dictionary only so that the copy of <c>StateTrieStoreAdapter</c>, which names
/// <c>AddressStorageNodeDictionary.AddressNodes</c> as the destination of an account's nodes, stays verbatim.
/// </remarks>
public static class AddressStorageNodeDictionary
{
    /// <summary>Where a <see cref="SnapshotBundle"/> writes the storage trie nodes of the account hashing to <paramref name="AddressHash"/>.</summary>
    public readonly record struct AddressNodes(Hash256 AddressHash);
}
