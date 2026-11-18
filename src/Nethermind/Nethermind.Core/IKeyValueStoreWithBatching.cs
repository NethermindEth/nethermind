// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core
{
    public interface IKeyValueStoreWithBatching : IKeyValueStore
    {
        IWriteBatch StartWriteBatch();

        /// <summary>Creates an abortable batch with atomic publication.</summary>
        /// <exception cref="System.NotSupportedException">The store does not support atomic publication.</exception>
        IAtomicWriteBatch StartAtomicWriteBatch() => ThrowAtomicWritesNotSupported();

        [DoesNotReturn, StackTraceHidden]
        private static IAtomicWriteBatch ThrowAtomicWritesNotSupported() =>
            throw new System.NotSupportedException("Atomic write batches are not supported by this store.");
    }
}
