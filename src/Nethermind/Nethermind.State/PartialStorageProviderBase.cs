// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State
{
    /// <summary>
    /// Contains common code for both Persistent and Transient storage providers
    /// </summary>
    internal abstract class PartialStorageProviderBase
    {
        private readonly Dictionary<InternalStorageKey, int> _intraBlockCache = new(16);
        private StackList<int>?[] _stackPool = new StackList<int>[512];
        private int _stackCount;
        protected readonly ILogger _logger;
        protected ChangeKey[] _changeKeys = new ChangeKey[1_024];
        protected StorageValue[] _changeValues = new StorageValue[1_024];
        protected int _changeCount;
        private readonly List<ChangeKey> _keptKeyCache = new();
        private readonly List<StorageValue> _keptValueCache = new();

        // stack of snapshot indexes on changes for start of each transaction
        // this is needed for OriginalValues for new transactions
        protected readonly Stack<int> _transactionChangesSnapshots = new();

        protected PartialStorageProviderBase(ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        /// <summary>
        /// Get the storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        [SkipLocalsInit]
        public StorageValue Get(in StorageCell storageCell)
        {
            return GetCurrentValue(in storageCell);
        }

        /// <summary>
        /// Set the provided value to storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        [SkipLocalsInit]
        public void Set(in StorageCell storageCell, StorageValue newValue)
        {
            PushUpdate(in storageCell, newValue);
        }

        /// <summary>
        /// Creates a restartable snapshot.
        /// </summary>
        /// <param name="newTransactionStart"> Indicates new transaction will start here.</param>
        /// <returns>Snapshot index</returns>
        public int TakeSnapshot(bool newTransactionStart)
        {
            int position = _changeCount - 1;
            if (_logger.IsTrace) _logger.Trace($"Storage snapshot {position}");
            if (newTransactionStart && position != Resettable.EmptyPosition)
            {
                _transactionChangesSnapshots.Push(position);
            }

            return position;
        }

        /// <summary>
        /// Restore the state to the provided snapshot
        /// </summary>
        /// <param name="snapshot">Snapshot index</param>
        /// <exception cref="InvalidOperationException">Throws exception if snapshot is invalid</exception>
        [SkipLocalsInit]
        public void Restore(int snapshot)
        {
            if (_logger.IsTrace) _logger.Trace($"Restoring storage snapshot {snapshot}");

            int currentPosition = _changeCount - 1;
            if (snapshot > currentPosition)
            {
                throw new InvalidOperationException($"{GetType().Name} tried to restore snapshot {snapshot} beyond current position {currentPosition}");
            }

            if (snapshot == currentPosition)
            {
                return;
            }

            Span<ChangeKey> keys = _changeKeys.AsSpan(0, _changeCount);
            ReadOnlySpan<StorageValue> values = _changeValues.AsSpan(0, _changeCount);
            for (int i = 0; i < currentPosition - snapshot; i++)
            {
                int pos = currentPosition - i;
                ref readonly ChangeKey changeKey = ref keys[pos];
                InternalStorageKey ikey = new(in changeKey.StorageCell);
                int stackIdx = _intraBlockCache[ikey];
                StackList<int> stack = _stackPool[stackIdx]!;
                if (stack.Count == 1)
                {
                    if (keys[stack.Peek()].ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = stack.Pop();
                        if (actualPosition != pos)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {currentPosition} - {i}");
                        }

                        _keptKeyCache.Add(changeKey);
                        _keptValueCache.Add(values[pos]);
                        keys[actualPosition] = default;
                        continue;
                    }
                }

                int forAssertion = stack.Pop();
                if (forAssertion != pos)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {currentPosition} - {i}");
                }

                keys[pos] = default;

                if (stack.Count == 0)
                {
                    _intraBlockCache.Remove(ikey);
                    stack.Return();
                    _stackPool[stackIdx] = null;
                }
            }

            _changeCount = snapshot + 1;
            ReadOnlySpan<ChangeKey> keptKeys = CollectionsMarshal.AsSpan(_keptKeyCache);
            for (int i = 0; i < keptKeys.Length; i++)
            {
                EnsureChangeCapacity();
                _changeKeys[_changeCount] = keptKeys[i];
                _changeValues[_changeCount] = _keptValueCache[i];
                InternalStorageKey ikeyKept = new(in keptKeys[i].StorageCell);
                _stackPool[_intraBlockCache[ikeyKept]]!.Push(_changeCount);
                _changeCount++;
            }

            _keptKeyCache.Clear();
            _keptValueCache.Clear();

            while (_transactionChangesSnapshots.TryPeek(out int lastOriginalSnapshot) && lastOriginalSnapshot > snapshot)
            {
                _transactionChangesSnapshots.Pop();
            }

        }

        /// <summary>
        /// Commit persistent storage
        /// </summary>
        /// <param name="stateTracer">State tracer</param>
        public void Commit(IStorageTracer tracer)
        {
            if (_changeCount == 0)
            {
                if (_logger.IsTrace) _logger.Trace("No storage changes to commit");
            }
            else
            {
                CommitCore(tracer);
            }
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
            if (_logger.IsTrace) _logger.Trace("Resetting storage");

            _changeCount = 0;
            _intraBlockCache.Clear();
            for (int i = 0; i < _stackCount; i++)
            {
                _stackPool[i]?.Return();
            }
            _stackCount = 0;
            _transactionChangesSnapshots.Clear();
        }

        /// <summary>
        /// Attempt to get the current value at the storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="value">Resulting value</param>
        /// <returns>True if value has been set</returns>
        [SkipLocalsInit]
        protected bool TryGetCachedValue(in StorageCell storageCell, out StorageValue value)
        {
            InternalStorageKey ikey = new(in storageCell);
            if (_intraBlockCache.TryGetValue(ikey, out int stackIdx))
            {
                int lastChangeIndex = _stackPool[stackIdx]!.Peek();
                {
                    value = _changeValues[lastChangeIndex];
                    return true;
                }
            }

            Unsafe.SkipInit(out value);
            return false;
        }

        /// <summary>
        /// Get the current value at the specified location
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at location</returns>
        protected abstract StorageValue GetCurrentValue(in StorageCell storageCell);

        /// <summary>
        /// Update the storage cell with provided value
        /// </summary>
        /// <param name="cell">Storage location</param>
        /// <param name="value">Value to set</param>
        [SkipLocalsInit]
        private void PushUpdate(in StorageCell cell, StorageValue value)
        {
            StackList<int> stack = SetupRegistry(cell);
            stack.Push(_changeCount);
            EnsureChangeCapacity();
            _changeKeys[_changeCount] = new ChangeKey(in cell, ChangeType.Update);
            _changeValues[_changeCount] = value;
            _changeCount++;
        }

        /// <summary>
        /// Initialize the StackList at the storage cell position if needed
        /// </summary>
        protected StackList<int> SetupRegistry(in StorageCell cell)
        {
            InternalStorageKey ikey = new(in cell);
            return SetupRegistry(in ikey);
        }

        /// <summary>
        /// Initialize the StackList at the storage key position if needed
        /// </summary>
        protected StackList<int> SetupRegistry(in InternalStorageKey ikey)
        {
            ref int stackIdx = ref CollectionsMarshal.GetValueRefOrAddDefault(_intraBlockCache, ikey, out bool exists);
            if (!exists)
            {
                stackIdx = AllocateStack();
            }

            return _stackPool[stackIdx]!;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int AllocateStack()
        {
            int idx = _stackCount;
            if (idx >= _stackPool.Length)
            {
                Array.Resize(ref _stackPool, _stackPool.Length * 2);
            }
            _stackPool[idx] = StackList<int>.Rent();
            _stackCount = idx + 1;
            return idx;
        }

        /// <summary>
        /// Try to get the StackList for a storage cell
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryGetStack(in StorageCell cell, out StackList<int> stack)
        {
            InternalStorageKey ikey = new(in cell);
            return TryGetStack(in ikey, out stack);
        }

        /// <summary>
        /// Try to get the StackList for an internal storage key
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryGetStack(in InternalStorageKey ikey, out StackList<int> stack)
        {
            if (_intraBlockCache.TryGetValue(ikey, out int idx))
            {
                stack = _stackPool[idx]!;
                return true;
            }
            stack = null;
            return false;
        }

        /// <summary>
        /// Get the StackList for a storage cell (must exist)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected StackList<int> GetStack(in StorageCell cell)
        {
            InternalStorageKey ikey = new(in cell);
            return _stackPool[_intraBlockCache[ikey]]!;
        }

        /// <summary>
        /// Get the StackList for an internal storage key (must exist)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected StackList<int> GetStack(in InternalStorageKey ikey)
        {
            return _stackPool[_intraBlockCache[ikey]]!;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void EnsureChangeCapacity()
        {
            if (_changeCount >= _changeKeys.Length)
                GrowChangeArrays();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowChangeArrays()
        {
            int newCap = _changeKeys.Length * 2;
            Array.Resize(ref _changeKeys, newCap);
            Array.Resize(ref _changeValues, newCap);
        }

        /// <summary>
        /// Clear all storage at specified address
        /// </summary>
        /// <param name="address">Contract address</param>
        public virtual void ClearStorage(Address address)
        {
            // We are setting cached values to zero so we do not use previously set values
            // when the contract is revived with CREATE2 inside the same block
            foreach (KeyValuePair<InternalStorageKey, int> entry in _intraBlockCache)
            {
                if (entry.Key.AddressEquals(address))
                {
                    Set(new StorageCell(address, entry.Key.Index), StorageValue.Zero);
                }
            }
        }

        /// <summary>
        /// Used for tracking each change to storage (key portion only; values stored separately)
        /// </summary>
        protected readonly struct ChangeKey(in StorageCell storageCell, ChangeType changeType)
        {
            public readonly StorageCell StorageCell = storageCell;
            public readonly ChangeType ChangeType = changeType;

            public bool IsNull => ChangeType == ChangeType.Null;
        }

        /// <summary>
        /// Type of change to track
        /// </summary>
        protected enum ChangeType
        {
            Null = 0,
            JustCache,
            Update,
        }

        /// <summary>
        /// Reference-free key for _intraBlockCache. Inlines the 20-byte address to avoid
        /// references, so Dictionary.Clear() skips entry zeroing entirely.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        protected readonly struct InternalStorageKey : IEquatable<InternalStorageKey>
        {
            private readonly Vector128<byte> _addrLo; // 16 bytes
            private readonly uint _addrHi;            // 4 bytes
            private readonly UInt256 _index;           // 32 bytes
            private readonly int _hash;               // 4 bytes
            // Total: 56 bytes, NO references

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public InternalStorageKey(in StorageCell cell)
            {
                ref byte addrBytes = ref MemoryMarshal.GetArrayDataReference(cell.Address.Bytes);
                _addrLo = Unsafe.As<byte, Vector128<byte>>(ref addrBytes);
                _addrHi = Unsafe.As<byte, uint>(ref Unsafe.Add(ref addrBytes, 16));
                _index = cell.Index;
                _hash = ComputeHash();
            }

            public UInt256 Index => _index;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool AddressEquals(Address address)
            {
                ref byte ab = ref MemoryMarshal.GetArrayDataReference(address.Bytes);
                return _addrLo == Unsafe.As<byte, Vector128<byte>>(ref ab)
                    && _addrHi == Unsafe.As<byte, uint>(ref Unsafe.Add(ref ab, 16));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Equals(InternalStorageKey other)
                => Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in _index)) ==
                   Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in other._index))
                && _addrLo == other._addrLo
                && _addrHi == other._addrHi;

            public override bool Equals(object? obj) => obj is InternalStorageKey other && Equals(other);

            [MethodImpl(MethodImplOptions.NoInlining)]
            private int ComputeHash()
            {
                int hash = MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _index), 1)).FastHash();
                ReadOnlySpan<byte> addrSpan = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<Vector128<byte>, byte>(ref Unsafe.AsRef(in _addrLo)), 20);
                return hash ^ addrSpan.FastHash();
            }

            public override int GetHashCode() => _hash;
        }
    }
}
