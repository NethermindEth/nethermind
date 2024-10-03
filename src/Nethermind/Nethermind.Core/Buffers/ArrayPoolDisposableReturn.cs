// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Core.Buffers;

public readonly struct ArrayPoolDisposableReturn : IDisposable
{
    private readonly byte[] _array;

    private ArrayPoolDisposableReturn(byte[] array) => _array = array;

    public static ArrayPoolDisposableReturn Rent(int size, out byte[] array)
    {
        array = ArrayPool<byte>.Shared.Rent(size);
        return new(array);
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_array);
}
