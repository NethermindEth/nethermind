
using System.Buffers;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core.Collections;

public readonly struct ArrayPoolSpan<T>(ArrayPool<T> arrayPool, int length) : IDisposable
{
    private readonly T[] _array = arrayPool.Rent(length);
    private readonly int _length = length;
    public ArrayPoolSpan(int length) : this(ArrayPool<T>.Shared, length) { }

    public readonly int Length => _length;
    public readonly ref T this[int index]
    {
        get
        {
            if (index > _length)
            {
                ThrowArgumentOutOfRangeException();
            }

            return ref _array[index];

            [DoesNotReturn]
            static void ThrowArgumentOutOfRangeException()
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    public static implicit operator Span<T>(ArrayPoolSpan<T> arrayPoolSpan) => arrayPoolSpan._array.AsSpan(0, arrayPoolSpan._length);
    public static implicit operator ReadOnlySpan<T>(ArrayPoolSpan<T> arrayPoolSpan) => arrayPoolSpan._array.AsSpan(0, arrayPoolSpan._length);

    public readonly void Dispose() => arrayPool.Return(_array);
}
