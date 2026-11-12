// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Stateless.Execution.IO;

public static partial class TrustedInput
{
    /// <summary>Whether the guest uses the items where they lie in its input.</summary>
    /// <remarks>
    /// Only where the input is read into writable memory, nothing ever moves or collects an object, and storing a
    /// reference is a plain store, so that an array whose header is written into the input is as good as any other:
    /// Airbender, which copies its input into RAM and whose runtime wraps every write barrier into a bare store.
    /// </remarks>
    private static bool UsesItemsInPlace => AirbenderFlag.IsActive;

    private static partial byte[] Item(ReadOnlySpan<byte> data, int start, int length)
    {
        ref byte first = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), start);
        return UsesItemsInPlace && IsAligned(ref first) ? ArrayAt(ref first, length) : data.Slice(start, length).ToArray();
    }

    private static partial ReadOnlyMemory<byte> Code(ReadOnlySpan<byte> data, int start, int length)
    {
        if (!UsesItemsInPlace) return ExecutableCodeMemory.Copy(data.Slice(start, length));

        Span<byte> codeAndPadding = MemoryMarshal.CreateSpan(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(data), start), length + CodeInfo.ExecutionPadding);
        return ExecutableCodeMemory.PlacePadded(codeAndPadding, length);
    }

    private static unsafe bool IsAligned(ref byte first) => ((nuint)Unsafe.AsPointer(ref first) & 7) == 0;

    /// <summary>Lays the header of a <see cref="T:byte[]"/> over the <see cref="ItemPrefixLength"/> bytes before
    /// <paramref name="first"/>, making the <paramref name="length"/> bytes from it that array.</summary>
    /// <remarks>The header is an object header word, the method table and the length, as the runtime lays out every
    /// array, read once off an array it allocated.</remarks>
    private static unsafe byte[] ArrayAt(ref byte first, int length)
    {
        if (ArrayLayout.MethodTable == 0) ArrayLayout.Read();

        byte* array = (byte*)Unsafe.AsPointer(ref first) - ArrayLayout.DataOffset;
        ((nint*)array)[-1] = 0;
        *(nint*)array = ArrayLayout.MethodTable;
        *(int*)(array + sizeof(nint)) = length;
        // A 64-bit runtime pads the length to the data.
        if (sizeof(nint) == 8) *(int*)(array + sizeof(nint) + sizeof(int)) = 0;
        nint reference = (nint)array;
        return Unsafe.As<nint, byte[]>(ref reference);
    }

    /// <summary>Where the runtime puts the parts of a <see cref="T:byte[]"/>.</summary>
    /// <remarks>A class of its own, with no initializer, so each access is a plain load rather than a class-initialization check.</remarks>
    private static class ArrayLayout
    {
        public static nint MethodTable;
        public static nint DataOffset;

        public static unsafe void Read()
        {
            byte[] probe = new byte[1];
            nint array = Unsafe.As<byte[], nint>(ref probe);
            DataOffset = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(probe)) - array;
            // The method table, then the length padded to a word; the object header word lies before the method table.
            if (DataOffset != 2 * sizeof(nint) || DataOffset + sizeof(nint) > ItemPrefixLength)
                throw new InvalidOperationException("Unexpected array layout");

            MethodTable = *(nint*)array;
        }
    }
}
