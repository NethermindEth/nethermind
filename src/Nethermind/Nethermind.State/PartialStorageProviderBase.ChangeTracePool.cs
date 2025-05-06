// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State;

internal partial class PartialStorageProviderBase
{
    /// <summary>
    /// A resettable pool of <see cref="ChangeTrace"/> objects.
    /// </summary>
    protected sealed class ChangeTracePool
    {
        private readonly uint _size;
        private const int DefaultSize = 512 * 1024;
        private UIntPtr ChangeTraceSize => ChangeTrace.MemorySize * _size;

        private readonly unsafe ChangeTrace* _values;
        private int _index;

        public unsafe ChangeTracePool(uint size = DefaultSize)
        {
            _index = 0;
            _size = size;
            _values = (ChangeTrace*)NativeMemory.AlignedAlloc(ChangeTraceSize, ChangeTrace.MemorySize);
            GC.AddMemoryPressure((long)ChangeTraceSize);
        }

        public unsafe ChangeTracePtr New()
        {
            Debug.Assert(_index < _size);

            var ptr = new ChangeTracePtr(_values + _index);
            _index++;
            return ptr;
        }

        public void Clear()
        {
            _index = 0;
        }
    }

    /// <summary>
    /// A not so managed reference to <see cref="ChangeTrace"/>.
    /// Allows quick reuse between the blocks.
    /// </summary>
    protected readonly unsafe struct ChangeTracePtr(ChangeTrace* pointer)
    {
        public ref ChangeTrace Ref => ref Unsafe.AsRef<ChangeTrace>(pointer);
    }
}
