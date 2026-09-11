// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Evm.Tracing.State;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// EIP-1153 provides a transient store for contracts that doesn't persist
    /// storage across calls. Reverts will rollback any transient state changes.
    /// </summary>
    /// <remarks>
    /// Values are held inline as 32-byte words rather than as <see cref="byte"/> arrays, so a TSTORE does
    /// not allocate. This does not share <c>PartialStorageProviderBase</c> because transient storage needs
    /// far less of it: there are no original values, no tree, nothing to commit, and no storage-clear
    /// journal entries — every change is a plain overwrite, so an undo log of the previous word is enough
    /// to restore any snapshot.
    /// </remarks>
    internal sealed class TransientStorageProvider(ILogManager? logManager)
    {
        private readonly Dictionary<StorageCell, Entry> _values = [];
        private readonly List<Undo> _undo = new(Resettable.StartCapacity);
        private readonly ILogger _logger = logManager?.GetClassLogger<TransientStorageProvider>() ?? throw new ArgumentNullException(nameof(logManager));

        /// <summary>A stored value: the word plus how many bytes of it were written.</summary>
        /// <remarks>The length is kept so a read returns exactly the bytes that were stored. Callers other
        /// than TSTORE may write fewer than 32, and the array this replaced round-tripped its own length.</remarks>
        private struct Entry
        {
            public ValueHash256 Value;
            public byte Length;
        }

        /// <summary>What a cell held before one write, so that write can be rolled back.</summary>
        private readonly struct Undo(in StorageCell storageCell, in Entry previous, bool existed)
        {
            public readonly StorageCell StorageCell = storageCell;
            public readonly Entry Previous = previous;
            public readonly bool Existed = existed;
        }

        /// <summary>Gets the transient value at the cell, or a single zero byte when it was never written.</summary>
        /// <remarks>The span points into the value table and is only valid until the next write, which is
        /// all TLOAD needs — it pushes the word before anything else can run.</remarks>
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(_values, storageCell);
            if (Unsafe.IsNullRef(ref entry)) return StorageTree.ZeroBytes;

            return MemoryMarshal
                .CreateReadOnlySpan(ref Unsafe.As<ValueHash256, byte>(ref entry.Value), ValueHash256.MemorySize)
                .Slice(ValueHash256.MemorySize - entry.Length);
        }

        public void Set(in StorageCell storageCell, byte[] newValue) => Set(in storageCell, (ReadOnlySpan<byte>)newValue);

        public void Set(in StorageCell storageCell, ReadOnlySpan<byte> newValue)
        {
            Debug.Assert(newValue.Length <= ValueHash256.MemorySize);

            Entry entry = default;
            entry.Length = (byte)newValue.Length;
            // Right-aligned, so the stored bytes read back unchanged and a full word needs no shifting.
            newValue.CopyTo(entry.Value.BytesAsSpan[(ValueHash256.MemorySize - newValue.Length)..]);
            Set(in storageCell, in entry);
        }

        private void Set(in StorageCell storageCell, in Entry entry)
        {
            ref Entry slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_values, storageCell, out bool exists);
            _undo.Add(new Undo(in storageCell, exists ? slot : default, exists));
            slot = entry;
        }

        /// <inheritdoc cref="PartialStorageProviderBase.TakeSnapshot"/>
        public int TakeSnapshot(bool newTransactionStart)
        {
            int position = _undo.Count - 1;
            if (_logger.IsTrace) _logger.Trace($"Storage snapshot {position}");
            return position;
        }

        /// <inheritdoc cref="PartialStorageProviderBase.Restore"/>
        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring transient storage snapshot {snapshot}");

            int currentPosition = _undo.Count - 1;
            if (snapshot > currentPosition)
            {
                throw new InvalidOperationException($"{nameof(TransientStorageProvider)} tried to restore snapshot {snapshot} beyond current position {currentPosition}");
            }

            Span<Undo> undo = CollectionsMarshal.AsSpan(_undo);
            for (int i = currentPosition; i > snapshot; i--)
            {
                ref readonly Undo entry = ref undo[i];
                if (entry.Existed)
                {
                    _values[entry.StorageCell] = entry.Previous;
                }
                else
                {
                    _values.Remove(entry.StorageCell);
                }
            }

            CollectionsMarshal.SetCount(_undo, snapshot + 1);
        }

        /// <summary>Transient storage does not outlive the transaction, so committing discards it.</summary>
        public void Commit(IStorageTracer tracer) => Reset();

        public void Reset(bool resetBlockChanges = true)
        {
            if (_logger.IsTrace) _logger.Trace("Resetting storage");
            _values.Clear();
            _undo.Clear();
        }

        /// <summary>Zeroes every cell of the address, revertibly.</summary>
        /// <remarks>Collects first rather than writing while enumerating, since a write can grow the table.</remarks>
        public void ClearStorage(Address address)
        {
            List<StorageCell>? toClear = null;
            foreach (StorageCell cell in _values.Keys)
            {
                if (cell.Address == address)
                {
                    (toClear ??= []).Add(cell);
                }
            }

            if (toClear is null) return;

            foreach (StorageCell cell in toClear)
            {
                Set(in cell, in ZeroEntry);
            }
        }

        /// <summary>Matches the single zero byte an unwritten cell reads as.</summary>
        private static readonly Entry ZeroEntry = new() { Value = default, Length = 1 };
    }
}
