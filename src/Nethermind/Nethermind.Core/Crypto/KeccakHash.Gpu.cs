// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ILGPU;
using ILGPU.Runtime;
using Nethermind.Core.Buffers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// ReSharper disable InconsistentNaming
namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    static readonly Context context = Context.CreateDefault();
    static readonly Device? device = CreateDevice();

    public static bool SupportsGpu => device != null;

    private static Device? CreateDevice()
    {
        Device device = context.GetPreferredDevice(preferCPU: false);
        if (device.AcceleratorType != AcceleratorType.CPU)
        {
            return device;
        }

        return null;
    }

    /// <summary>
    /// Prints information on the given accelerator.
    /// </summary>
    /// <param name="accelerator">The target accelerator.</param>
    static void PrintAcceleratorInfo(Accelerator accelerator)
    {
        Console.WriteLine($"Name: {accelerator.Name}");
        Console.WriteLine($"MemorySize: {accelerator.MemorySize}");
        Console.WriteLine($"MaxThreadsPerGroup: {accelerator.MaxNumThreadsPerGroup}");
        Console.WriteLine($"MaxSharedMemoryPerGroup: {accelerator.MaxSharedMemoryPerGroup}");
        Console.WriteLine($"MaxGridSize: {accelerator.MaxGridSize}");
        Console.WriteLine($"MaxConstantMemory: {accelerator.MaxConstantMemory}");
        Console.WriteLine($"WarpSize: {accelerator.WarpSize}");
        Console.WriteLine($"NumMultiprocessors: {accelerator.NumMultiprocessors}");
    }

    static object _lock = new object();
    static Action<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<ulong>>? _keccakKernel;
    static Accelerator? selectedAccelerator;

    private static Action<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<ulong>> GetKeccakKernel(Accelerator selectedAccelerator)
    {
        var keccakKernel = _keccakKernel;
        if (keccakKernel is not null) return keccakKernel;

        lock (_lock)
        {
            keccakKernel = _keccakKernel;
            if (keccakKernel is not null) return keccakKernel;

            keccakKernel = selectedAccelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<int>,
                ArrayView<byte>,
                ArrayView<ulong>>(
                Keccak256Kernel);

            _keccakKernel = keccakKernel;
            return keccakKernel;
        }
    }

    public unsafe static void ComputeHashBatchGpu(List<ReadOnlyMemory<byte>> inputs, Span<ValueHash256> output)
    {
        if (device == null)
        {
            throw new InvalidOperationException("No suitable GPU accelerator found.");
        }

        lock (_lock)
        {
            selectedAccelerator ??= device.CreateAccelerator(context);
            //PrintAcceleratorInfo(selectedAccelerator);
            var dataLength = 0;
            foreach (ReadOnlyMemory<byte> input in CollectionsMarshal.AsSpan(inputs))
            {
                dataLength += input.Length;
            }

            byte[] data = ArrayPool<byte>.Shared.Rent(dataLength);
            int[] offsets = ArrayPool<int>.Shared.Rent(inputs.Count);

            int index = 0;
            dataLength = 0;
            foreach (ReadOnlyMemory<byte> input in CollectionsMarshal.AsSpan(inputs))
            {
                input.Span.CopyTo(data.AsSpan(dataLength, input.Length));
                // Skip first offset as always 0
                if (index > 0)
                {
                    offsets[index - 1] = dataLength;
                }
                dataLength += input.Length;
                index++;
            }
            // Add total length as final offset
            offsets[index - 1] = dataLength;

            using var outputBuffer = selectedAccelerator.Allocate1D<ulong>(inputs.Count * 4);

            var keccakKernel = GetKeccakKernel(selectedAccelerator);
            fixed (byte* startInput = &MemoryMarshal.GetArrayDataReference(data))
            fixed (int* startOffsets = &MemoryMarshal.GetArrayDataReference(offsets))
            {
                using var offsetsBuffer = selectedAccelerator.Allocate1D<int>(inputs.Count);
                using var inputBuffer = selectedAccelerator.Allocate1D<byte>(dataLength);

                inputBuffer.View.CopyFromCPU(ref Unsafe.AsRef<byte>(startInput), dataLength);
                offsetsBuffer.View.CopyFromCPU(ref Unsafe.AsRef<int>(startOffsets), inputs.Count);

                keccakKernel(
                    inputs.Count,
                    offsetsBuffer.View,
                    inputBuffer.View,
                    outputBuffer.View);

                selectedAccelerator.Synchronize();
            }

            ArrayPool<int>.Shared.Return(offsets);
            ArrayPool<byte>.Shared.Return(data);

            ref var outputRef = ref MemoryMarshal.GetReference(output);
            fixed (ValueHash256* start = &outputRef)
            {
                outputBuffer.View.CopyToCPU<ulong>(ref Unsafe.AsRef<ulong>(start), output.Length * 4);
            }
        }
    }

    public unsafe static void ComputeHashBatchGpu(List<CappedArray<byte>> inputs, Span<ValueHash256> output)
    {
        if (device == null)
        {
            throw new InvalidOperationException("No suitable GPU accelerator found.");
        }

        lock (_lock)
        {
            selectedAccelerator ??= device.CreateAccelerator(context);
            PrintAcceleratorInfo(selectedAccelerator);
            var dataLength = 0;
            foreach (CappedArray<byte> input in CollectionsMarshal.AsSpan(inputs))
            {
                dataLength += input.Length;
            }

            byte[] data = ArrayPool<byte>.Shared.Rent(dataLength);
            int[] offsets = ArrayPool<int>.Shared.Rent(inputs.Count);

            int index = 0;
            dataLength = 0;
            foreach (CappedArray<byte> input in CollectionsMarshal.AsSpan(inputs))
            {
                input.AsSpan().CopyTo(data.AsSpan(dataLength, input.Length));
                // Skip first offset as always 0
                if (index > 0)
                {
                    offsets[index - 1] = dataLength;
                }
                dataLength += input.Length;
                index++;
            }
            // Add total length as final offset
            offsets[index - 1] = dataLength;

            using var outputBuffer = selectedAccelerator.Allocate1D<ulong>(inputs.Count * 4);

            var keccakKernel = GetKeccakKernel(selectedAccelerator);
            fixed (byte* startInput = &MemoryMarshal.GetArrayDataReference(data))
            fixed (int* startOffsets = &MemoryMarshal.GetArrayDataReference(offsets))
            {
                using var offsetsBuffer = selectedAccelerator.Allocate1D<int>(inputs.Count);
                using var inputBuffer = selectedAccelerator.Allocate1D<byte>(dataLength);

                inputBuffer.View.CopyFromCPU(ref Unsafe.AsRef<byte>(startInput), dataLength);
                offsetsBuffer.View.CopyFromCPU(ref Unsafe.AsRef<int>(startOffsets), inputs.Count);

                keccakKernel(
                    inputs.Count,
                    offsetsBuffer.View,
                    inputBuffer.View,
                    outputBuffer.View);

                selectedAccelerator.Synchronize();
            }

            ArrayPool<int>.Shared.Return(offsets);
            ArrayPool<byte>.Shared.Return(data);

            ref var outputRef = ref MemoryMarshal.GetReference(output);
            fixed (ValueHash256* start = &outputRef)
            {
                outputBuffer.View.CopyToCPU<ulong>(ref Unsafe.AsRef<ulong>(start), output.Length * 4);
            }
        }
    }

    public static void Keccak256Kernel(
        Index1D index,
        ArrayView<int> offsets,
        ArrayView<byte> inputs,
        ArrayView<ulong> outputs)
    {
        // Get the input subview for the current thread
        int inputOffset = index == 0 ? 0 : offsets[index - 1];
        var inputLength = offsets[index] - inputOffset;
        var input = inputs.SubView(inputOffset, inputLength);

        // Initialize state array
        ulong[] state = new ulong[25];

        const int rateInBytes = 136; // Keccak-f[1600] with rate 1088 bits (136 bytes)
        int iBlock = 0;

        // Process all full blocks
        while (iBlock + rateInBytes <= inputLength)
        {
            for (int j = 0; j < rateInBytes; j++)
            {
                int idx = j >> 3; // j / 8
                int shift = (j & 7) << 3; // (j % 8) * 8
                ulong value = ((ulong)input[iBlock + j]) << shift;
                state[idx] ^= value;
            }
            KeccakPermutation(state);
            iBlock += rateInBytes;
        }

        // Process the final, possibly incomplete block
        int blockSize = inputLength - iBlock;
        for (int j = 0; j < blockSize; j++)
        {
            int idx = j >> 3;
            int shift = (j & 7) << 3;
            ulong value = ((ulong)input[iBlock + j]) << shift;
            state[idx] ^= value;
        }

        // Padding
        int idxPad = blockSize >> 3;
        int shiftPad = (blockSize & 7) << 3;
        state[idxPad] ^= 0x01UL << shiftPad; // Append bit '1' after remaining input
        state[16] ^= 0x8000000000000000UL; // Set the last bit of the block to '1'

        // Apply permutation after padding
        KeccakPermutation(state);

        // Squeeze phase: extract the hash
        outputs[index * 4 + 0] = state[0];
        outputs[index * 4 + 1] = state[1];
        outputs[index * 4 + 2] = state[2];
        outputs[index * 4 + 3] = state[3];
    }

    /// <summary>
    /// Performs the Keccak-f[1600] permutation on the state.
    /// </summary>
    /// <param name="state">The 25-element state array.</param>
    static void KeccakPermutation(ulong[] state)
    {
        // Round constants for Keccak-f[1600]
        ulong[] RC = new ulong[] {
            0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL,
            0x8000000080008000UL, 0x000000000000808BUL, 0x0000000080000001UL,
            0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008AUL,
            0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
            0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL,
            0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
            0x000000000000800AUL, 0x800000008000000AUL, 0x8000000080008081UL,
            0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
        };

        ulong[] C = new ulong[5];
        ulong[] D = new ulong[5];
        ulong[] B = new ulong[25];

        for (int round = 0; round < 24; round++)
        {
            // Theta step
            for (int x = 0; x < 5; x++)
            {
                C[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
            }

            for (int x = 0; x < 5; x++)
            {
                D[x] = C[(x + 4) % 5] ^ RotateLeft(C[(x + 1) % 5], 1);
            }

            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 25; y += 5)
                {
                    state[y + x] ^= D[x];
                }
            }

            // Rho and Pi steps
            B[0] = state[0];
            B[1] = RotateLeft(state[6], 44);
            B[2] = RotateLeft(state[12], 43);
            B[3] = RotateLeft(state[18], 21);
            B[4] = RotateLeft(state[24], 14);
            B[5] = RotateLeft(state[3], 28);
            B[6] = RotateLeft(state[9], 20);
            B[7] = RotateLeft(state[10], 3);
            B[8] = RotateLeft(state[16], 45);
            B[9] = RotateLeft(state[22], 61);
            B[10] = RotateLeft(state[1], 1);
            B[11] = RotateLeft(state[7], 6);
            B[12] = RotateLeft(state[13], 25);
            B[13] = RotateLeft(state[19], 8);
            B[14] = RotateLeft(state[20], 18);
            B[15] = RotateLeft(state[4], 27);
            B[16] = RotateLeft(state[5], 36);
            B[17] = RotateLeft(state[11], 10);
            B[18] = RotateLeft(state[17], 15);
            B[19] = RotateLeft(state[23], 56);
            B[20] = RotateLeft(state[2], 62);
            B[21] = RotateLeft(state[8], 55);
            B[22] = RotateLeft(state[14], 39);
            B[23] = RotateLeft(state[15], 41);
            B[24] = RotateLeft(state[21], 2);

            // Chi step
            for (int y = 0; y < 25; y += 5)
            {
                ulong t0 = B[y + 0];
                ulong t1 = B[y + 1];
                ulong t2 = B[y + 2];
                ulong t3 = B[y + 3];
                ulong t4 = B[y + 4];

                state[y + 0] = t0 ^ ((~t1) & t2);
                state[y + 1] = t1 ^ ((~t2) & t3);
                state[y + 2] = t2 ^ ((~t3) & t4);
                state[y + 3] = t3 ^ ((~t4) & t0);
                state[y + 4] = t4 ^ ((~t0) & t1);
            }

            // Iota step
            state[0] ^= RC[round];
        }
    }

    static ulong RotateLeft(ulong value, int offset)
    {
        return (value << offset) | (value >> (64 - offset));
    }

}
