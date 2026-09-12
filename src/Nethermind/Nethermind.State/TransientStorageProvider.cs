// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// EIP-1153 provides a transient store for contracts that doesn't persist
    /// storage across calls. Reverts will rollback any transient state changes.
    /// </summary>
    internal sealed class TransientStorageProvider(ILogManager logManager) : PartialStorageProviderBase(logManager)
    {

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Set(in StorageCell cell, in UInt256 value)
        {
            if (_intraBlockCache.Count == 0 && value.IsZero) return;
            ref HeadChange head = ref CollectionsMarshal.GetValueRefOrAddDefault(_intraBlockCache, cell, out bool exists);
            if (exists && value == head.Value) return;
            if (!exists && value.IsZero)
            {
                _intraBlockCache.Remove(cell);
                return;
            }
            PushUpdate(in cell, in value, ref head, exists);
        }

        protected override void ClearSlot(in StorageCell cell, ref HeadChange head, bool exists)
        {
            if (!head.Value.IsZero) base.ClearSlot(in cell, ref head, exists);
        }

        /// <summary>
        /// Get the storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="value">Value at cell</param>
        protected override void GetCurrentValue(in StorageCell storageCell, out UInt256 value) =>
            TryGetCachedValue(in storageCell, out value);
    }
}
