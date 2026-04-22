// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;

namespace Nethermind.Core.Collections;

/// <summary>
/// Provides a zkEVM-safe shared array pool. Under zkEVM, returns a simple allocating pool
/// that avoids the complex generic internals of <see cref="ArrayPool{T}"/>.
/// Under normal execution, returns <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
public static class SafeArrayPool<T>
{
#if ZK_EVM
    public static readonly ArrayPool<T> Shared = new SimplePool();

    private sealed class SimplePool : ArrayPool<T>
    {
        public override T[] Rent(int minimumLength) => new T[minimumLength];
        public override void Return(T[] array, bool clearArray = false) { }
    }
#else
    public static readonly ArrayPool<T> Shared = ArrayPool<T>.Shared;
#endif
}
