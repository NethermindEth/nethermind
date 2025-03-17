using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions;

public static class MemoryExtensions
{
    /// <summary>
    /// Try to get the underlying array if possible to prevent copying.
    /// </summary>
    /// <param name="memory"></param>
    /// <returns></returns>
    public static byte[]? AsArray(this in Memory<byte>? memory)
    {
        if (memory is null) return null;

        return memory.Value.AsArray();
    }

    public static byte[] AsArray(this in Memory<byte> memory)
    {
        if (
            MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) &&
            segment.Offset == 0 && segment.Count == segment.Array!.Length
        ) return segment.Array;

        return memory.Span.ToArray();
    }

    public static Memory<T> TakeAndMove<T>(this ref Memory<T> memory, int length)
    {
        var m = memory[..length];
        memory = memory[length..];
        return m;
    }

    public static ReadOnlyMemory<T> TakeAndMove<T>(this ref ReadOnlyMemory<T> memory, int length)
    {
        var m = memory[..length];
        memory = memory[length..];
        return m;
    }

    public static void Clear<T>(this ref Memory<T> memory)
    {
        memory.Span.Clear();
    }
}
