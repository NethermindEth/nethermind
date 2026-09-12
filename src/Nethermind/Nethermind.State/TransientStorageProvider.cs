// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// EIP-1153 provides a transient store for contracts that doesn't persist
    /// storage across calls. Reverts will rollback any transient state changes.
    /// </summary>
    internal sealed class TransientStorageProvider(ILogManager logManager) : PartialStorageProviderBase(logManager, coalesceUpdates: true)
    {

        /// <summary>Copies a changed transient value into the journal.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(in StorageCell cell, ReadOnlySpan<byte> value)
        {
            if (_intraBlockCache.Count == 0 && IsZero(value)) return;
            ref HeadChange head = ref CollectionsMarshal.GetValueRefOrAddDefault(_intraBlockCache, cell, out bool exists);
            if (exists && value.SequenceEqual(head.Value)) return;

            bool isZero = IsZero(value);
            if (isZero && (!exists || head.Value.AsSpan().IsZero()))
            {
                if (!exists) _intraBlockCache.Remove(cell);
                return;
            }

            PushUpdate(in cell, isZero ? StorageTree.ZeroBytes : value.ToArray(), ref head, exists);
        }

        /// <inheritdoc/>
        public override void Set(in StorageCell cell, byte[] value) => base.Set(in cell, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsZero(ReadOnlySpan<byte> value)
            => value.Length == 32 ? MemoryMarshal.Read<UInt256>(value).IsZero : value.IsZero();

        /// <summary>
        /// Get the storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        protected override ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell) =>
            TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes : StorageTree.ZeroBytes;
    }
}
