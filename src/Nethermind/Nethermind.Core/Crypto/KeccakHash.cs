// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    private const int HASH_SIZE = 32;
    private const int STATE_SIZE = 200;
    private const int HASH_DATA_AREA = 136;

    private byte[] _remainderBuffer = [];
    private ulong[] _state = [];
    private byte[]? _hash;
    private int _remainderLength;
    private int _roundSize;

    private KeccakHash(KeccakHash original)
    {
        if (original._state.Length > 0)
        {
            _state = Pool.RentState();
            original._state.AsSpan().CopyTo(_state);
        }

        if (original._remainderBuffer.Length > 0)
        {
            _remainderBuffer = Pool.RentRemainder();
            original._remainderBuffer.AsSpan().CopyTo(_remainderBuffer);
        }

        _roundSize = original._roundSize;
        _remainderLength = original._remainderLength;
        HashSize = original.HashSize;
    }

    private KeccakHash(int size)
    {
        // Verify the size
        if (size <= 0 || size > STATE_SIZE)
        {
            throw new ArgumentException($"Invalid Keccak hash size. Must be between 0 and {STATE_SIZE}.");
        }

        _roundSize = STATE_SIZE == size ? HASH_DATA_AREA : checked(STATE_SIZE - (2 * size));
        _remainderLength = 0;
        HashSize = size;
    }

    public static KeccakHash Create(int size = HASH_SIZE) => new(size);

    /// <summary>
    /// The current hash buffer at this point. Recomputed after hash updates.
    /// </summary>
    public byte[] Hash => _hash ??= GenerateHash(); // If the hash is null, recalculate.

    /// <summary>
    /// Indicates the hash size in bytes.
    /// </summary>
    public int HashSize { get; private set; }

    public KeccakHash Copy() => new(this);

    public static void ComputeHash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if ((uint)(output.Length - 1) >= STATE_SIZE)
            ThrowInvalidOutputSize(output.Length);

#if ZK_EVM
        if (output.Length == HASH_SIZE)
        {
            ComputeHash256(input, output);
            return;
        }
#endif
        int roundSize = GetRoundSize(output.Length);

        Span<ulong> state = stackalloc ulong[STATE_SIZE / sizeof(ulong)];
        Span<byte> stateBytes = MemoryMarshal.AsBytes(state);

        if (input.Length == Address.Size)
        {
            // Hashing Address, 20 bytes which is uint+Vector128
            Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(stateBytes)) =
                Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(input));
            Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(stateBytes), sizeof(uint))) =
                    Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(input), sizeof(uint)));
        }
        else if (input.Length == Vector256<byte>.Count)
        {
            // Hashing Hash256 or UInt256, 32 bytes
            Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(stateBytes)) =
                Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(input));
        }
        else if (input.Length >= roundSize)
        {
            // Process full rounds
            do
            {
                XorVectors(stateBytes, input[..roundSize]);
                KeccakF(state);
                input = input[roundSize..];
            } while (input.Length >= roundSize);

            if (input.Length > 0)
            {
                // XOR the remaining input bytes into the state
                XorVectors(stateBytes, input);
            }
        }
        else
        {
            input.CopyTo(stateBytes);
        }

        // Apply terminator markers within the current block
        stateBytes[input.Length] ^= 0x01;  // Append bit '1' after remaining input
        stateBytes[roundSize - 1] ^= 0x80; // Set the last bit of the round to '1'

        // Process the final block
        KeccakF(state);

        if (output.Length == Vector256<byte>.Count)
        {
            // Fast Vector sized copy for Hash256
            Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(output)) =
                Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(stateBytes));
        }
        else if (output.Length == Vector512<byte>.Count)
        {
            // Fast Vector sized copy for Hash512
            Unsafe.As<byte, Vector512<byte>>(ref MemoryMarshal.GetReference(output)) =
                Unsafe.As<byte, Vector512<byte>>(ref MemoryMarshal.GetReference(stateBytes));
        }
        else
        {
            stateBytes[..output.Length].CopyTo(output);
        }
    }

    public static byte[] ComputeHashBytes(ReadOnlySpan<byte> input, int size = HASH_SIZE)
    {
        byte[] output = new byte[size];
        ComputeHash(input, output);
        return output;
    }

    public static void ComputeHashBytesToSpan(ReadOnlySpan<byte> input, Span<byte> output) =>
        ComputeHash(input, output);

    public static void ComputeUIntsToUint(Span<uint> input, Span<uint> output) =>
        ComputeHash(MemoryMarshal.Cast<uint, byte>(input), MemoryMarshal.Cast<uint, byte>(output));

    public static uint[] ComputeUIntsToUint(Span<uint> input, int size)
    {
        uint[] output = new uint[size / sizeof(uint)];
        ComputeUIntsToUint(input, output);
        return output;
    }

    public static uint[] ComputeBytesToUint(ReadOnlySpan<byte> input, int size)
    {
        uint[] output = new uint[size / sizeof(uint)];
        ComputeHash(input, MemoryMarshal.Cast<uint, byte>(output.AsSpan()));
        return output;
    }

    public ValueHash256 GenerateValueHash()
    {
        Unsafe.SkipInit(out ValueHash256 output);
        UpdateFinalTo(output.BytesAsSpan);
        return output;
    }

    public void Update(ReadOnlySpan<byte> input)
    {
        if (_hash is not null)
            ThrowHashingComplete();

        // If the size is zero, quit
        if (input.Length == 0)
            return;

        ulong[] state = _state;
        int offset = 0;
        Span<byte> stateBytes = default;

        // Handle any existing remainder
        if (_remainderLength != 0)
        {
            int bytesToFill = _roundSize - _remainderLength;
            int bytesToCopy = Math.Min(input.Length, bytesToFill);

            input[..bytesToCopy].CopyTo(_remainderBuffer.AsSpan(_remainderLength));
            _remainderLength += bytesToCopy;
            offset += bytesToCopy;

            if (_remainderLength == _roundSize)
            {
                if (state.Length == 0)
                {
                    // Delay state allocation until we actually need a permutation.
                    _state = state = Pool.RentState();
                }

                stateBytes = MemoryMarshal.AsBytes(state.AsSpan());
                // XOR the remainder buffer into the state using XorVectors
                XorVectors(stateBytes, _remainderBuffer);
                KeccakF(state);

                // Reset remainder
                _remainderLength = 0;
                Pool.ReturnRemainder(ref _remainderBuffer);
            }
        }

        // Process full rounds
        if (input.Length - offset >= _roundSize)
        {
            if (state.Length == 0)
            {
                // Delay state allocation until we actually need a permutation.
                _state = state = Pool.RentState();
            }

            if (stateBytes.IsEmpty)
            {
                stateBytes = MemoryMarshal.AsBytes(state.AsSpan());
            }

            do
            {
                XorVectors(stateBytes, input.Slice(offset, _roundSize));
                KeccakF(state);

                offset += _roundSize;
            } while (input.Length - offset >= _roundSize);
        }

        // Handle remaining input (less than a full block)
        int remainingInputLength = input.Length - offset;
        if (remainingInputLength > 0)
        {
            if (_remainderBuffer.Length == 0)
            {
                _remainderBuffer = Pool.RentRemainder();
            }

            input[offset..].CopyTo(_remainderBuffer);
            _remainderLength = remainingInputLength;
        }
    }

    public void UpdateFinalTo(Span<byte> output)
    {
        if (_hash is not null)
            ThrowHashingComplete();

#if ZK_EVM
        if (_state.Length == 0 && _roundSize == HASH_DATA_AREA && output.Length == HASH_SIZE)
        {
            ComputeHash256(_remainderBuffer.AsSpan(0, _remainderLength), output);
            Pool.ReturnRemainder(ref _remainderBuffer);

            _remainderLength = 0;
            return;
        }
#endif
        ulong[] state = _state;

        if (state.Length == 0)
            _state = state = Pool.RentState();

        Span<byte> stateBytes = MemoryMarshal.AsBytes(state.AsSpan());

        if (_remainderLength > 0)
        {
            // XOR the remainder buffer into the state
            XorVectors(stateBytes, _remainderBuffer.AsSpan(0, _remainderLength));
        }

        // Apply terminator markers within the current block
        stateBytes[_remainderLength] ^= 0x01; // Append bit '1' after the input
        stateBytes[_roundSize - 1] ^= 0x80;   // Set the last bit of the block to '1'

        KeccakF(state);

        // Copy the hash output
        if (output.Length == Vector256<byte>.Count)
        {
            // Fast Vector sized copy for Hash256
            Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(output)) =
                Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(stateBytes));
        }
        else if (output.Length == Vector512<byte>.Count)
        {
            // Fast Vector sized copy for Hash512
            Unsafe.As<byte, Vector512<byte>>(ref MemoryMarshal.GetReference(output)) =
                Unsafe.As<byte, Vector512<byte>>(ref MemoryMarshal.GetReference(stateBytes));
        }
        else
        {
            stateBytes[..output.Length].CopyTo(output);
        }

        Pool.ReturnState(ref _state);
    }

    public void Reset()
    {
        // Clear our hash state information.
        Pool.ReturnState(ref _state);
        Pool.ReturnRemainder(ref _remainderBuffer);
        _hash = null;
    }

    public void ResetTo(KeccakHash original)
    {
        if (original._remainderBuffer.Length == 0)
        {
            Pool.ReturnRemainder(ref _remainderBuffer);
        }
        else
        {
            if (_remainderBuffer.Length == 0)
            {
                _remainderBuffer = Pool.RentRemainder();
            }
            original._remainderBuffer.AsSpan().CopyTo(_remainderBuffer);
        }

        _roundSize = original._roundSize;
        _remainderLength = original._remainderLength;

        if (original._state.Length == 0)
        {
            Pool.ReturnState(ref _state);
        }
        else
        {
            if (_state.Length == 0)
            {
                // Original allocated, but not here, so allocated
                _state = Pool.RentState();
            }
            // Copy from original
            original._state.AsSpan().CopyTo(_state);
        }

        HashSize = original.HashSize;
        _hash = null;
    }

    private static partial void KeccakF(Span<ulong> st);

    private static int GetRoundSize(int hashSize) => checked(STATE_SIZE - 2 * hashSize);

    private byte[] GenerateHash()
    {
        byte[] output = new byte[HashSize];
        UpdateFinalTo(output);
        // Obtain the state data in the desired (hash) size we want.
        _hash = output;

        // Return the result.
        return output;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void XorVectors(Span<byte> state, ReadOnlySpan<byte> input)
    {
        ref byte stateRef = ref MemoryMarshal.GetReference(state);
        if (Vector512.IsHardwareAccelerated && input.Length >= Vector512<byte>.Count)
        {
            // Convert to uint for the mod else the Jit does a more complicated signed mod
            // whereas as uint it just does an And
            int vectorLength = input.Length - (int)((uint)input.Length % (uint)Vector512<byte>.Count);
            ref byte inputRef = ref MemoryMarshal.GetReference(input);
            for (int i = 0; i < vectorLength; i += Vector512<byte>.Count)
            {
                ref Vector512<byte> state256 = ref Unsafe.As<byte, Vector512<byte>>(ref Unsafe.Add(ref stateRef, i));
                Vector512<byte> input256 = Unsafe.As<byte, Vector512<byte>>(ref Unsafe.Add(ref inputRef, i));
                state256 = Vector512.Xor(state256, input256);
            }

            if (input.Length == vectorLength) return;

            input = input[vectorLength..];
            stateRef = ref Unsafe.Add(ref stateRef, vectorLength);
        }

        if (Vector256.IsHardwareAccelerated && input.Length >= Vector256<byte>.Count)
        {
            // Convert to uint for the mod else the Jit does a more complicated signed mod
            // whereas as uint it just does an And
            int vectorLength = input.Length - (int)((uint)input.Length % (uint)Vector256<byte>.Count);
            ref byte inputRef = ref MemoryMarshal.GetReference(input);
            for (int i = 0; i < vectorLength; i += Vector256<byte>.Count)
            {
                ref Vector256<byte> state256 = ref Unsafe.As<byte, Vector256<byte>>(ref Unsafe.Add(ref stateRef, i));
                Vector256<byte> input256 = Unsafe.As<byte, Vector256<byte>>(ref Unsafe.Add(ref inputRef, i));
                state256 = Vector256.Xor(state256, input256);
            }

            if (input.Length == vectorLength) return;

            input = input[vectorLength..];
            stateRef = ref Unsafe.Add(ref stateRef, vectorLength);
        }

        if (Vector128.IsHardwareAccelerated && input.Length >= Vector128<byte>.Count)
        {
            int vectorLength = input.Length - (int)((uint)input.Length % (uint)Vector128<byte>.Count);
            ref byte inputRef = ref MemoryMarshal.GetReference(input);
            for (int i = 0; i < vectorLength; i += Vector128<byte>.Count)
            {
                ref Vector128<byte> state128 = ref Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref stateRef, i));
                Vector128<byte> input128 = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref inputRef, i));
                state128 = Vector128.Xor(state128, input128);
            }

            if (input.Length == vectorLength) return;

            input = input[vectorLength..];
            stateRef = ref Unsafe.Add(ref stateRef, vectorLength);
        }

        // As 25 longs in state, 1 more to process after the vector sizes
        if (input.Length >= sizeof(ulong))
        {
            int ulongLength = input.Length - (int)((uint)input.Length % sizeof(ulong));
            ref byte inputRef = ref MemoryMarshal.GetReference(input);
            for (int i = 0; i < ulongLength; i += sizeof(ulong))
            {
                ref ulong state64 = ref Unsafe.As<byte, ulong>(ref Unsafe.Add(ref stateRef, i));
                ulong input64 = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref inputRef, i));
                state64 ^= input64;
            }

            // Should exit here for 25 longs
            if (input.Length == ulongLength) return;

            input = input[ulongLength..];
            stateRef = ref Unsafe.Add(ref stateRef, ulongLength);
        }

        // Handle remaining bytes
        for (int i = 0; i < input.Length; i++)
        {
            Unsafe.Add(ref stateRef, i) ^= input[i];
        }
    }

    [DoesNotReturn]
    private static void ThrowHashingComplete() => throw new CryptographicException("Keccak hash is complete.");

    [DoesNotReturn]
    private static void ThrowInvalidOutputSize(int length) => throw new ArgumentOutOfRangeException(
        nameof(length), length, $"Must be between 1 and {STATE_SIZE}.");

    private static class Pool
    {
        private const int MaxPooledPerThread = 4;
        [ThreadStatic]
        private static Queue<byte[]>? s_remainderCache;

        public static byte[] RentRemainder() => s_remainderCache?.TryDequeue(out byte[]? remainder)
            ?? false ? remainder : new byte[STATE_SIZE];

        public static void ReturnRemainder(ref byte[] remainder)
        {
            if (remainder.Length == 0)
                return;

            Queue<byte[]> cache = (s_remainderCache ??= new());

            if (cache.Count <= MaxPooledPerThread)
            {
                remainder.AsSpan().Clear();
                cache.Enqueue(remainder);
            }

            remainder = [];
        }

        [ThreadStatic]
        private static Queue<ulong[]>? s_stateCache;

        public static ulong[] RentState() => s_stateCache?.TryDequeue(out ulong[]? state)
            ?? false ? state : new ulong[STATE_SIZE / sizeof(ulong)];

        public static void ReturnState(ref ulong[] state)
        {
            if (state.Length == 0)
                return;

            Queue<ulong[]> cache = (s_stateCache ??= new());

            if (cache.Count <= MaxPooledPerThread)
            {
                state.AsSpan().Clear();
                cache.Enqueue(state);
            }

            state = [];
        }
    }
}
