// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Test-only convenience overloads for <see cref="IPersistence.IPersistenceReader"/> that iterate
/// the full key range. Production callers always pass explicit bounds, so these whole-range
/// forwarders live with the tests rather than on the production interface.
/// </summary>
internal static class FlatPersistenceTestExtensions
{
    public static IPersistence.IFlatIterator CreateAccountIterator(this IPersistence.IPersistenceReader reader)
        => reader.CreateAccountIterator(ValueKeccak.Zero, ValueKeccak.MaxValue);

    public static IPersistence.IFlatIterator CreateStorageIterator(this IPersistence.IPersistenceReader reader, in ValueHash256 accountKey)
        => reader.CreateStorageIterator(accountKey, ValueKeccak.Zero, ValueKeccak.MaxValue);
}
