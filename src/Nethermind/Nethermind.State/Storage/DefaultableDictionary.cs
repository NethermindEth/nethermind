// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.State;

internal sealed class DefaultableDictionary()
{
    private bool _missingAreDefault;
    private readonly Dictionary<UInt256, StorageChangeTrace> _dictionary = new(Comparer.Instance);
    public int EstimatedSize => _dictionary.Count;
    public int Capacity => _dictionary.Capacity;

    public void Reset()
    {
        _missingAreDefault = false;
        _dictionary.Clear();
    }
    public void ClearAndSetMissingAsDefault()
    {
        _missingAreDefault = true;
        _dictionary.Clear();
    }

    public ref StorageChangeTrace GetValueRefOrAddDefault(UInt256 storageCellIndex, out bool exists)
    {
        ref StorageChangeTrace value = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, storageCellIndex, out exists);
        if (!exists && _missingAreDefault)
        {
            // Where we know the rest of the tree is empty
            // we can say the value was found but is default
            // rather than having to check the database
            value = StorageChangeTrace.ZeroBytes;
            exists = true;
        }
        return ref value;
    }

    public ref StorageChangeTrace GetValueRefOrNullRef(UInt256 storageCellIndex)
        => ref CollectionsMarshal.GetValueRefOrNullRef(_dictionary, storageCellIndex);

    public StorageChangeTrace this[UInt256 key]
    {
        set => _dictionary[key] = value;
    }

    public Dictionary<UInt256, StorageChangeTrace>.Enumerator GetEnumerator() => _dictionary.GetEnumerator();

    private sealed class Comparer : IEqualityComparer<UInt256>
    {
        public static Comparer Instance { get; } = new();

        private Comparer() { }

        public bool Equals(UInt256 x, UInt256 y)
            => Unsafe.As<UInt256, Vector256<byte>>(ref x) == Unsafe.As<UInt256, Vector256<byte>>(ref y);

        public int GetHashCode([DisallowNull] UInt256 obj)
            => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
    }
}
