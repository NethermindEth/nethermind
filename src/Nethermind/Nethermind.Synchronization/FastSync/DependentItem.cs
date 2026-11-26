// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading;

namespace Nethermind.Synchronization.FastSync
{
    [DebuggerDisplay("{SyncItem.Hash} {Counter}")]
    internal class DependentItem(StateSyncItem syncItem, byte[] value, int counter, bool isAccount = false)
    {
        private int _counter = counter;

        public StateSyncItem SyncItem { get; } = syncItem;
        public byte[] Value { get; } = value;
        public int Counter => Volatile.Read(ref _counter);

        public bool IsAccount { get; } = isAccount;

        /// <summary>Marks one dependency of this item as resolved.</summary>
        /// <returns><c>true</c> for the call that resolves the last outstanding dependency.</returns>
        /// <remarks>
        /// Responses are handled on several threads, so a child saved from another response can resolve its
        /// dependency while the thread that queued it is still resolving the rest.
        /// </remarks>
        public bool ResolveDependency() => Interlocked.Decrement(ref _counter) == 0;
    }
}
