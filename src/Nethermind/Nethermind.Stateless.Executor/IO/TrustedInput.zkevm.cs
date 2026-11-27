// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Zkvm.Abstractions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Stateless.Execution.IO;

public static partial class TrustedInput
{
    /// <summary>Uses the item where it lies in the input when the guest allows it, else copies it.</summary>
    /// <remarks>
    /// Only where the input is read into writable memory that outlives the run, nothing ever moves or collects an
    /// object, and storing a reference to an object outside the managed heap is sound, so that an array whose header is
    /// written into the input is as good as any other: Airbender, which copies its input into RAM and whose runtime wraps
    /// every write barrier into a bare store; SP1, whose stateless input keeps its state nodes in the input as such
    /// arrays already; and OpenVM, which reads its input into a buffer it never frees and runs the same runtime as SP1.
    /// Not ZisK, whose input region is read-only. An item is used in place only at an 8-aligned address: SP1 rejects
    /// misaligned accesses, and its witness-node keccak rounds an array's data pointer down to 8 without testing it.
    /// </remarks>
    private static partial byte[] Item(ReadOnlySpan<byte> data, int start, int length)
    {
        ref byte first = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), start);
        return (AirbenderFlag.IsActive || FirstTouchMemoryFlag.IsActive || OpenVmFlag.IsActive) && IsAligned(ref first)
            ? ArrayAt(ref first, length)
            : data.Slice(start, length).ToArray();
    }

    private static partial ReadOnlyMemory<byte> Code(ReadOnlySpan<byte> data, int start, int length)
    {
        if (AirbenderFlag.IsActive)
        {
            Span<byte> codeAndPadding = MemoryMarshal.CreateSpan(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(data), start), length + CodeInfo.ExecutionPadding);
            return ExecutableCodeMemory.PlacePadded(codeAndPadding, length);
        }

        if (FirstTouchMemoryFlag.IsActive || OpenVmFlag.IsActive)
        {
            ref byte first = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), start);
            if (IsAligned(ref first))
            {
                if (OpenVmFlag.IsActive)
                    return ExecutableCodeMemory.WrapInputBuffer(ArrayAt(ref first, length + CodeInfo.ExecutionPadding), length);

                return ExecutableCodeMemory.PlacePadded(MemoryMarshal.CreateSpan(ref first, length + CodeInfo.ExecutionPadding), length);
            }
        }

        return ExecutableCodeMemory.Copy(data.Slice(start, length));
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

    private static partial bool TryTakePrehashedCode(ReadOnlySpan<byte> code, int index, out ValueHash256 hash)
    {
        hash = default;
        if (!AirbenderFlag.IsActive) return false;
        unsafe
        {
            fixed (byte* data = code)
            fixed (ValueHash256* output = &hash)
            {
                return Accelerators.AirbenderPrehashedCode(data, (uint)index, (byte*)output);
            }
        }
    }

    private static unsafe partial IOwnedReadOnlyList<byte[]> ReadStateNodes(ReadOnlySpan<byte> data)
    {
        if (AirbenderFlag.IsActive)
        {
            (int position, int count) = ListStart(data, StateNodesList);
            byte* first, last;
            uint read;
            byte* input = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(data));
            if (count > 0 && Accelerators.AirbenderMptNodes(&first, &last, &read)
                && read == (uint)count && first == input + position + ItemPrefixLength)
            {
                // Airbender's input lives in nonmoving RAM for the entire guest execution.
                int length = data.Length;
                return new AirbenderWitnessNodes(first, last, count,
                    () => ReadItems(new ReadOnlySpan<byte>(input, length), StateNodesList, 0));
            }
        }

        return new ItemList(ReadItems(data, StateNodesList, 0));
    }

    private static partial bool RetainHints => AirbenderFlag.IsActive;
}
