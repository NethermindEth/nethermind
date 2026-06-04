// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Synchronization.FastSync
{
    [DebuggerDisplay("{SyncItem.Hash} {Counter}")]
    internal class DependentItem(StateSyncItem syncItem, byte[] value, int counter, bool isAccount = false)
    {
        public StateSyncItem SyncItem { get; } = syncItem;
        public byte[] Value { get; } = value;
        public int Counter { get; set; } = counter;

        public bool IsAccount { get; } = isAccount;
    }
}
