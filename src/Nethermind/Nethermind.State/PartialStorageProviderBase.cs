// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Evm.Tracing.State;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// Contains common code for both Persistent and Transient storage providers
    /// </summary>
    internal abstract class PartialStorageProviderBase
    {
        protected readonly Dictionary<StorageCell, int> _slotIndex = new();
        protected byte[][] _slotsHot = new byte[Resettable.StartCapacity][];
        protected SlotMeta[] _slotsMeta = new SlotMeta[Resettable.StartCapacity];
        protected UndoEntry[] _undoBuffer = new UndoEntry[Resettable.StartCapacity];
        protected int[] _dirtySlotIndices = new int[Resettable.StartCapacity];
        protected int[] _frameUndoOffsets = new int[32];
        protected int[] _frameIds = new int[32];

        protected int _slotCount;
        protected int _undoCount;
        protected int _dirtyCount;
        protected int _dirtyGeneration = 1;
        protected int _frameCount = 1;
        protected int _currentFrameId;
        protected readonly ILogger _logger;

        // stack of snapshot tokens for start of each transaction
        // this is needed for OriginalValues for new transactions
        protected readonly Stack<int> _transactionStartSnapshots = new();

        protected PartialStorageProviderBase(ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _frameUndoOffsets[0] = 0;
            _frameIds[0] = 0;
        }

        /// <summary>
        /// Get the storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        public ReadOnlySpan<byte> Get(in StorageCell storageCell)
        {
            return GetCurrentValue(in storageCell);
        }

        /// <summary>
        /// Set the provided value to storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            int slotIndex = GetOrCreateSlotIndex(storageCell);
            MutateSlot(slotIndex, newValue, SlotFlags.None, SlotFlags.None, setDirty: true);
        }

        /// <summary>
        /// Creates a restartable snapshot.
        /// </summary>
        /// <param name="newTransactionStart"> Indicates new transaction will start here.</param>
        /// <returns>Snapshot index</returns>
        public int TakeSnapshot(bool newTransactionStart)
        {
            int snapshot = _currentFrameId;
            int nextFrameId = snapshot + 1;
            EnsureFrameCapacity(_frameCount + 1);
            _frameUndoOffsets[_frameCount] = _undoCount;
            _frameIds[_frameCount] = nextFrameId;
            _frameCount++;
            _currentFrameId = nextFrameId;
            if (_logger.IsTrace) Trace(snapshot);
            if (newTransactionStart && _undoCount > 0)
            {
                _transactionStartSnapshots.Push(nextFrameId);
            }

            return snapshot;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(int position) => _logger.Trace($"Storage snapshot {position}");
        }

        /// <summary>
        /// Restore the state to the provided snapshot
        /// </summary>
        /// <param name="snapshot">Snapshot index</param>
        /// <exception cref="InvalidOperationException">Throws exception if snapshot is invalid</exception>
        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) Trace(snapshot);

            if (snapshot > _currentFrameId)
            {
                ThrowCannotRestore(snapshot, _currentFrameId);
            }

            int startOffset;
            int keptFrameCount;
            if (snapshot < 0)
            {
                // Revert everything including the base frame.
                startOffset = 0;
                keptFrameCount = 0;
                snapshot = 0;
            }
            else
            {
                keptFrameCount = _frameCount;
                while (keptFrameCount > 0 && _frameIds[keptFrameCount - 1] > snapshot)
                {
                    keptFrameCount--;
                }

                if (keptFrameCount == _frameCount)
                {
                    if (_undoCount > 0
                        && keptFrameCount > 0
                        && _undoCount > _frameUndoOffsets[keptFrameCount - 1])
                    {
                        startOffset = _frameUndoOffsets[keptFrameCount - 1];
                        keptFrameCount--;
                    }
                    else
                    {
                        return;
                    }
                }
                else if (keptFrameCount == 0)
                {
                    startOffset = 0;
                }
                else
                {
                    startOffset = _frameUndoOffsets[keptFrameCount];
                }
            }

            int undoEnd = _undoCount;
            for (int frameIndex = _frameCount - 1; frameIndex >= keptFrameCount; frameIndex--)
            {
                int frameStart = _frameUndoOffsets[frameIndex];
                for (int i = frameStart; i < undoEnd; i++)
                {
                    ref readonly UndoEntry undoEntry = ref _undoBuffer[i];
                    int slotIndex = undoEntry.SlotIndex;
                    _slotsHot[slotIndex] = undoEntry.PreviousValue;
                    ref SlotMeta meta = ref _slotsMeta[slotIndex];
                    meta.OwnerFrameId = undoEntry.PreviousOwnerFrameId;
                    meta.Flags = undoEntry.PreviousFlags;
                }

                undoEnd = frameStart;
            }

            _undoCount = startOffset;
            _frameCount = keptFrameCount > 0 ? keptFrameCount : 1;
            RebuildDirtySlotsAfterRestore();

            while (_transactionStartSnapshots.TryPeek(out int lastOriginalSnapshot) && lastOriginalSnapshot > snapshot)
            {
                _transactionStartSnapshots.Pop();
            }

            // Always push a fresh frame after restore (even when no undo entries were
            // replayed). Trimming frames changes the frame topology; without a new boundary,
            // subsequent mutations create undo entries scoped to the wrong frame, causing a
            // later deeper Restore to replay too many entries.
            int newFrameId = _currentFrameId + 1;
            EnsureFrameCapacity(_frameCount + 1);
            _frameUndoOffsets[_frameCount] = startOffset;
            _frameIds[_frameCount] = newFrameId;
            _frameCount++;
            _currentFrameId = newFrameId;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(int snapshot) => _logger.Trace($"Restoring storage snapshot {snapshot}");

            [DoesNotReturn, StackTraceHidden]
            void ThrowCannotRestore(int snapshot, int currentPosition)
                => throw new InvalidOperationException($"{GetType().Name} tried to restore snapshot {snapshot} beyond current position {currentPosition}");
        }

        /// <summary>
        /// Commit persistent storage
        /// </summary>
        /// <param name="stateTracer">State tracer</param>
        public void Commit(IStorageTracer tracer)
        {
            if (_slotCount == 0)
            {
                if (_logger.IsTrace) Trace();
            }
            else
            {
                CommitCore(tracer);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace() => _logger.Trace("No storage changes to commit");
        }

        /// <summary>
        /// Called by Commit
        /// Used for storage-specific logic
        /// </summary>
        /// <param name="tracer">Storage tracer</param>
        protected virtual void CommitCore(IStorageTracer tracer) => Reset();

        /// <summary>
        /// Reset the storage state
        /// </summary>
        public virtual void Reset(bool resetBlockChanges = true) => Reset();

        private void Reset()
        {
            if (_logger.IsTrace) Trace();

            int previousSlotCount = _slotCount;
            int previousUndoCount = _undoCount;

            if (previousSlotCount > 0)
            {
                Array.Clear(_slotsHot, 0, previousSlotCount);
                Array.Clear(_slotsMeta, 0, previousSlotCount);
            }

            if (previousUndoCount > 0)
            {
                Array.Clear(_undoBuffer, 0, previousUndoCount);
            }

            _slotIndex.Clear();
            _transactionStartSnapshots.Clear();

            _slotCount = 0;
            _undoCount = 0;
            _dirtyCount = 0;
            _dirtyGeneration = 1;
            _frameCount = 1;
            _currentFrameId = 0;
            _frameUndoOffsets[0] = 0;
            _frameIds[0] = 0;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace() => _logger.Trace("Resetting storage");
        }

        /// <summary>
        /// Attempt to get the current value at the storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="bytes">Resulting value</param>
        /// <returns>True if value has been set</returns>
        protected bool TryGetCachedValue(in StorageCell storageCell, out byte[]? bytes)
        {
            if (_slotIndex.TryGetValue(storageCell, out int slotIndex))
            {
                bytes = _slotsHot[slotIndex];
                if (bytes is not null)
                {
                    return true;
                }
            }

            bytes = null;
            return false;
        }

        /// <summary>
        /// Get the current value at the specified location
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at location</returns>
        protected abstract ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell);

        protected int GetOrCreateSlotIndex(in StorageCell storageCell)
        {
            if (_slotIndex.TryGetValue(storageCell, out int slotIndex))
            {
                return slotIndex;
            }

            slotIndex = _slotCount;
            EnsureSlotCapacity(slotIndex + 1);
            _slotCount = slotIndex + 1;
            _slotIndex[storageCell] = slotIndex;
            _slotsMeta[slotIndex] = new SlotMeta
            {
                Cell = storageCell,
                Flags = SlotFlags.None,
                OwnerFrameId = -1,
                DirtyGeneration = 0,
            };

            return slotIndex;
        }

        protected bool TryGetSlotIndex(in StorageCell storageCell, out int slotIndex)
            => _slotIndex.TryGetValue(storageCell, out slotIndex);

        protected bool TryGetTransactionSnapshot(out int snapshot)
            => _transactionStartSnapshots.TryPeek(out snapshot);

        protected bool TryGetUndoOffsetForSnapshot(int snapshot, out int offset)
        {
            for (int i = _frameCount - 1; i >= 0; i--)
            {
                if (_frameIds[i] == snapshot)
                {
                    offset = _frameUndoOffsets[i];
                    return true;
                }
            }

            offset = 0;
            return false;
        }

        protected byte[]? FindValueBeforeOffset(int slotIndex, int undoStartOffset)
        {
            byte[]? previous = null;
            for (int i = _undoCount - 1; i >= undoStartOffset; i--)
            {
                UndoEntry undoEntry = _undoBuffer[i];
                if (undoEntry.SlotIndex == slotIndex)
                {
                    previous = undoEntry.PreviousValue;
                }
            }

            return previous;
        }

        protected void CacheReadSlot(int slotIndex, byte[] value)
            => MutateSlot(slotIndex, value, SlotFlags.Read, SlotFlags.None, setDirty: false);

        protected void MutateSlot(int slotIndex, byte[] value, SlotFlags setFlags, SlotFlags clearFlags, bool setDirty)
        {
            ref SlotMeta meta = ref _slotsMeta[slotIndex];

            if (meta.OwnerFrameId < _currentFrameId)
            {
                SaveUndoAndUpdateFrame(slotIndex, ref meta);
            }

            SlotFlags flags = (meta.Flags | setFlags) & ~clearFlags;
            if (setDirty)
            {
                flags |= SlotFlags.Dirty;
                if (meta.DirtyGeneration != _dirtyGeneration)
                {
                    TrackDirty(slotIndex, ref meta);
                }
            }

            meta.Flags = flags;
            _slotsHot[slotIndex] = value;
        }

        private void SaveUndoAndUpdateFrame(int slotIndex, ref SlotMeta meta)
        {
            EnsureUndoCapacity(_undoCount + 1);
            _undoBuffer[_undoCount++] = new UndoEntry(slotIndex, meta.OwnerFrameId, meta.Flags, _slotsHot[slotIndex]);
            meta.OwnerFrameId = _currentFrameId;
        }

        private void TrackDirty(int slotIndex, ref SlotMeta meta)
        {
            EnsureDirtyCapacity(_dirtyCount + 1);
            meta.DirtyGeneration = _dirtyGeneration;
            _dirtySlotIndices[_dirtyCount++] = slotIndex;
        }

        private void RebuildDirtySlotsAfterRestore()
        {
            AdvanceDirtyGeneration();
            _dirtyCount = 0;
            for (int i = 0; i < _slotCount; i++)
            {
                ref SlotMeta meta = ref _slotsMeta[i];
                if ((meta.Flags & SlotFlags.Dirty) != 0)
                {
                    EnsureDirtyCapacity(_dirtyCount + 1);
                    _dirtySlotIndices[_dirtyCount++] = i;
                    meta.DirtyGeneration = _dirtyGeneration;
                }
                else
                {
                    meta.DirtyGeneration = 0;
                }
            }
        }

        private void AdvanceDirtyGeneration()
        {
            if (_dirtyGeneration == int.MaxValue)
            {
                _dirtyGeneration = 1;
                for (int i = 0; i < _slotCount; i++)
                {
                    _slotsMeta[i].DirtyGeneration = 0;
                }
            }
            else
            {
                _dirtyGeneration++;
            }
        }

        private void EnsureSlotCapacity(int requiredSize)
        {
            if (_slotsHot.Length >= requiredSize) return;

            int newLength = _slotsHot.Length;
            while (newLength < requiredSize)
            {
                newLength <<= 1;
            }

            Array.Resize(ref _slotsHot, newLength);
            Array.Resize(ref _slotsMeta, newLength);
        }

        private void EnsureUndoCapacity(int requiredSize)
        {
            if (_undoBuffer.Length >= requiredSize) return;

            int newLength = _undoBuffer.Length;
            while (newLength < requiredSize)
            {
                newLength <<= 1;
            }

            Array.Resize(ref _undoBuffer, newLength);
        }

        private void EnsureDirtyCapacity(int requiredSize)
        {
            if (_dirtySlotIndices.Length >= requiredSize) return;

            int newLength = _dirtySlotIndices.Length;
            while (newLength < requiredSize)
            {
                newLength <<= 1;
            }

            Array.Resize(ref _dirtySlotIndices, newLength);
        }

        private void EnsureFrameCapacity(int requiredSize)
        {
            if (_frameUndoOffsets.Length >= requiredSize) return;

            int newLength = _frameUndoOffsets.Length;
            while (newLength < requiredSize)
            {
                newLength <<= 1;
            }

            Array.Resize(ref _frameUndoOffsets, newLength);
            Array.Resize(ref _frameIds, newLength);
        }

        /// <summary>
        /// Clear all storage at specified address
        /// </summary>
        /// <param name="address">Contract address</param>
        public virtual void ClearStorage(Address address)
        {
            // We are setting cached values to zero so we do not use previously set values
            // when the contract is revived with CREATE2 inside the same block
            for (int i = 0; i < _slotCount; i++)
            {
                if (_slotsHot[i] is not null && _slotsMeta[i].Cell.Address == address)
                {
                    Set(_slotsMeta[i].Cell, StorageTree.ZeroBytes);
                }
            }
        }

        [Flags]
        protected enum SlotFlags : byte
        {
            None = 0,
            Dirty = 1 << 0,
            Read = 1 << 1,
        }

        protected struct SlotMeta
        {
            public StorageCell Cell;
            public SlotFlags Flags;
            public int OwnerFrameId;
            public int DirtyGeneration;
        }

        protected readonly struct UndoEntry
        {
            public readonly int SlotIndex;
            public readonly int PreviousOwnerFrameId;
            public readonly SlotFlags PreviousFlags;
            public readonly byte[]? PreviousValue;

            public UndoEntry(int slotIndex, int previousOwnerFrameId, SlotFlags previousFlags, byte[]? previousValue)
            {
                SlotIndex = slotIndex;
                PreviousOwnerFrameId = previousOwnerFrameId;
                PreviousFlags = previousFlags;
                PreviousValue = previousValue;
            }
        }
    }
}
