// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using static System.Numerics.BitOperations;

// ReSharper disable InconsistentNaming
namespace Nethermind.Core.Crypto
{
    public sealed class KeccakHash
    {
        public const int HASH_SIZE = 32;
        private const int STATE_SIZE = 200;
        private const int HASH_DATA_AREA = 136;
        private const int ROUNDS = 24;
        private const int LANE_BITS = 8 * 8;
        private const int TEMP_BUFF_SIZE = 144;

        private static readonly ulong[] RoundConstants =
        {
            0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL,
            0x8000000080008000UL, 0x000000000000808bUL, 0x0000000080000001UL,
            0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008aUL,
            0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
            0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL,
            0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
            0x000000000000800aUL, 0x800000008000000aUL, 0x8000000080008081UL,
            0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
        };

        private byte[] _remainderBuffer = Array.Empty<byte>();
        private ulong[] _state = Array.Empty<ulong>();
        private byte[]? _hash;
        private int _remainderLength;
        private int _roundSize;

        private static int GetRoundSize(int hashSize) => checked(STATE_SIZE - 2 * hashSize);

        /// <summary>
        /// Indicates the hash size in bytes.
        /// </summary>
        public int HashSize { get; private set; }

        /// <summary>
        /// The current hash buffer at this point. Recomputed after hash updates.
        /// </summary>
        public byte[] Hash => _hash ??= GenerateHash(); // If the hash is null, recalculate.

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

        public KeccakHash Copy() => new(this);

        public static KeccakHash Create(int size = HASH_SIZE) => new(size);

        // update the state with given number of rounds
        private static void KeccakF(Span<ulong> st)
        {
            if (Avx512F.IsSupported)
            {
                KeccakF1600Avx512F(st);
            }
            else
            {
                KeccakF1600(st);
            }
        }

        private static void KeccakF1600(Span<ulong> st)
        {
            Debug.Assert(st.Length == 25);

            ulong aba, abe, abi, abo, abu;
            ulong aga, age, agi, ago, agu;
            ulong aka, ake, aki, ako, aku;
            ulong ama, ame, ami, amo, amu;
            ulong asa, ase, asi, aso, asu;
            ulong bCa, bCe, bCi, bCo, bCu;
            ulong da, de, di, @do, du;
            ulong eba, ebe, ebi, ebo, ebu;
            ulong ega, ege, egi, ego, egu;
            ulong eka, eke, eki, eko, eku;
            ulong ema, eme, emi, emo, emu;
            ulong esa, ese, esi, eso, esu;

            asu = st[24];
            aso = st[23];
            asi = st[22];
            ase = st[21];
            asa = st[20];
            amu = st[19];
            amo = st[18];
            ami = st[17];
            ame = st[16];
            ama = st[15];
            aku = st[14];
            ako = st[13];
            aki = st[12];
            ake = st[11];
            aka = st[10];
            agu = st[9];
            ago = st[8];
            agi = st[7];
            age = st[6];
            aga = st[5];
            abu = st[4];
            abo = st[3];
            abi = st[2];
            abe = st[1];
            aba = st[0];

            for (int round = 0; round < ROUNDS; round += 2)
            {
                //    prepareTheta
                bCa = aba ^ aga ^ aka ^ ama ^ asa;
                bCe = abe ^ age ^ ake ^ ame ^ ase;
                bCi = abi ^ agi ^ aki ^ ami ^ asi;
                bCo = abo ^ ago ^ ako ^ amo ^ aso;
                bCu = abu ^ agu ^ aku ^ amu ^ asu;

                //thetaRhoPiChiIotaPrepareTheta(round  , A, E)
                da = bCu ^ RotateLeft(bCe, 1);
                de = bCa ^ RotateLeft(bCi, 1);
                di = bCe ^ RotateLeft(bCo, 1);
                @do = bCi ^ RotateLeft(bCu, 1);
                du = bCo ^ RotateLeft(bCa, 1);

                bCa = aba ^ da;
                bCe = RotateLeft(age ^ de, 44);
                bCi = RotateLeft(aki ^ di, 43);
                eba = bCa ^ ((~bCe) & bCi) ^ RoundConstants[round];
                bCo = RotateLeft(amo ^ @do, 21);
                ebe = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(asu ^ du, 14);
                ebi = bCi ^ ((~bCo) & bCu);
                ebo = bCo ^ ((~bCu) & bCa);
                ebu = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(abo ^ @do, 28);
                bCe = RotateLeft(agu ^ du, 20);
                bCi = RotateLeft(aka ^ da, 3);
                ega = bCa ^ ((~bCe) & bCi);
                bCo = RotateLeft(ame ^ de, 45);
                ege = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(asi ^ di, 61);
                egi = bCi ^ ((~bCo) & bCu);
                ego = bCo ^ ((~bCu) & bCa);
                egu = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(abe ^ de, 1);
                bCe = RotateLeft(agi ^ di, 6);
                bCi = RotateLeft(ako ^ @do, 25);
                eka = bCa ^ ((~bCe) & bCi);
                bCo = RotateLeft(amu ^ du, 8);
                eke = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(asa ^ da, 18);
                eki = bCi ^ ((~bCo) & bCu);
                eko = bCo ^ ((~bCu) & bCa);
                eku = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(abu ^ du, 27);
                bCe = RotateLeft(aga ^ da, 36);
                bCi = RotateLeft(ake ^ de, 10);
                ema = bCa ^ ((~bCe) & bCi);
                bCo = RotateLeft(ami ^ di, 15);
                eme = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(aso ^ @do, 56);
                emi = bCi ^ ((~bCo) & bCu);
                emo = bCo ^ ((~bCu) & bCa);
                emu = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(abi ^ di, 62);
                bCe = RotateLeft(ago ^ @do, 55);
                bCi = RotateLeft(aku ^ du, 39);
                esa = bCa ^ ((~bCe) & bCi);
                bCo = RotateLeft(ama ^ da, 41);
                ese = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(ase ^ de, 2);
                esi = bCi ^ ((~bCo) & bCu);
                eso = bCo ^ ((~bCu) & bCa);
                esu = bCu ^ ((~bCa) & bCe);

                //    prepareTheta

                bCe = ebe ^ ege ^ eke ^ eme ^ ese;
                bCu = ebu ^ egu ^ eku ^ emu ^ esu;
                //thetaRhoPiChiIotaPrepareTheta(round+1, E, A)
                da = bCu ^ RotateLeft(bCe, 1);
                bCa = eba ^ ega ^ eka ^ ema ^ esa;
                bCi = ebi ^ egi ^ eki ^ emi ^ esi;
                de = bCa ^ RotateLeft(bCi, 1);
                bCo = ebo ^ ego ^ eko ^ emo ^ eso;
                di = bCe ^ RotateLeft(bCo, 1);
                @do = bCi ^ RotateLeft(bCu, 1);
                du = bCo ^ RotateLeft(bCa, 1);


                bCi = RotateLeft(eki ^ di, 43);
                bCe = RotateLeft(ege ^ de, 44);
                bCa = eba ^ da;
                aba = bCa ^ ((~bCe) & bCi) ^ RoundConstants[round + 1];
                bCo = RotateLeft(emo ^ @do, 21);
                abe = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(esu ^ du, 14);
                abi = bCi ^ ((~bCo) & bCu);
                abo = bCo ^ ((~bCu) & bCa);
                abu = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(ebo ^ @do, 28);
                bCe = RotateLeft(egu ^ du, 20);
                bCi = RotateLeft(eka ^ da, 3);
                aga = bCa ^ ((~bCe) & bCi);
                bCo = RotateLeft(eme ^ de, 45);
                age = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(esi ^ di, 61);
                agi = bCi ^ ((~bCo) & bCu);
                ago = bCo ^ ((~bCu) & bCa);
                agu = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(ebe ^ de, 1);
                bCe = RotateLeft(egi ^ di, 6);
                bCi = RotateLeft(eko ^ @do, 25);
                aka = bCa ^ ((~bCe) & bCi);
                bCo = RotateLeft(emu ^ du, 8);
                ake = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(esa ^ da, 18);
                aki = bCi ^ ((~bCo) & bCu);
                ako = bCo ^ ((~bCu) & bCa);
                aku = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(ebu ^ du, 27);
                bCe = RotateLeft(ega ^ da, 36);
                bCi = RotateLeft(eke ^ de, 10);
                ama = bCa ^ ((~bCe) & bCi);
                bCo = RotateLeft(emi ^ di, 15);
                ame = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(eso ^ @do, 56);
                ami = bCi ^ ((~bCo) & bCu);
                amo = bCo ^ ((~bCu) & bCa);
                amu = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(ebi ^ di, 62);
                bCe = RotateLeft(ego ^ @do, 55);
                bCi = RotateLeft(eku ^ du, 39);
                asa = bCa ^ ((~bCe) & bCi);
                bCo = RotateLeft(ema ^ da, 41);
                ase = bCe ^ ((~bCi) & bCo);
                bCu = RotateLeft(ese ^ de, 2);
                asi = bCi ^ ((~bCo) & bCu);
                aso = bCo ^ ((~bCu) & bCa);
                asu = bCu ^ ((~bCa) & bCe);
            }

            //copyToState(state, A)
            st[24] = asu;
            st[23] = aso;
            st[22] = asi;
            st[21] = ase;
            st[20] = asa;
            st[19] = amu;
            st[18] = amo;
            st[17] = ami;
            st[16] = ame;
            st[15] = ama;
            st[14] = aku;
            st[13] = ako;
            st[12] = aki;
            st[11] = ake;
            st[10] = aka;
            st[9] = agu;
            st[8] = ago;
            st[7] = agi;
            st[6] = age;
            st[5] = aga;
            st[4] = abu;
            st[3] = abo;
            st[2] = abi;
            st[1] = abe;
            st[0] = aba;
        }

        public static Span<byte> ComputeHash(ReadOnlySpan<byte> input, int size = HASH_SIZE)
        {
            Span<byte> output = new byte[size];
            ComputeHash(input, output);
            return output;
        }

        public static byte[] ComputeHashBytes(ReadOnlySpan<byte> input, int size = HASH_SIZE)
        {
            byte[] output = new byte[size];
            ComputeHash(input, output);
            return output;
        }

        public static void ComputeHashBytesToSpan(ReadOnlySpan<byte> input, Span<byte> output)
        {
            ComputeHash(input, output);
        }

        public static void ComputeUIntsToUint(Span<uint> input, Span<uint> output)
        {
            ComputeHash(MemoryMarshal.Cast<uint, byte>(input), MemoryMarshal.Cast<uint, byte>(output));
        }

        public static uint[] ComputeUIntsToUint(Span<uint> input, int size)
        {
            uint[] output = new uint[size / sizeof(uint)];
            ComputeUIntsToUint(input, output);
            return output;
        }

        public static uint[] ComputeBytesToUint(ReadOnlySpan<byte> input, int size)
        {
            uint[] output = new uint[size / sizeof(uint)];
            ComputeHash(input, MemoryMarshal.Cast<uint, byte>(output));
            return output;
        }

        // compute a Keccak hash (md) of given byte length from "in"
        public static void ComputeHash(ReadOnlySpan<byte> input, Span<byte> output)
        {
            int size = output.Length;
            int roundSize = GetRoundSize(size);
            if (output.Length <= 0 || output.Length > STATE_SIZE)
            {
                ThrowBadKeccak();
            }

            Span<ulong> state = stackalloc ulong[STATE_SIZE / sizeof(ulong)];
            Span<byte> temp = stackalloc byte[TEMP_BUFF_SIZE];

            int remainingInputLength = input.Length;
            for (; remainingInputLength >= roundSize; remainingInputLength -= roundSize, input = input[roundSize..])
            {
                ReadOnlySpan<ulong> input64 = MemoryMarshal.Cast<byte, ulong>(input[..roundSize]);

                for (int i = 0; i < input64.Length; i++)
                {
                    state[i] ^= input64[i];
                }

                KeccakF(state);
            }

            // last block and padding
            if (input.Length >= TEMP_BUFF_SIZE || input.Length > roundSize || roundSize + 1 >= TEMP_BUFF_SIZE || roundSize == 0 || roundSize - 1 >= TEMP_BUFF_SIZE)
            {
                ThrowBadKeccak();
            }

            input[..remainingInputLength].CopyTo(temp);
            temp[remainingInputLength] = 1;
            temp[roundSize - 1] |= 0x80;

            Span<ulong> tempU64 = MemoryMarshal.Cast<byte, ulong>(temp[..roundSize]);
            for (int i = 0; i < tempU64.Length; i++)
            {
                state[i] ^= tempU64[i];
            }

            KeccakF(state);
            MemoryMarshal.AsBytes(state[..(size / sizeof(ulong))]).CopyTo(output);
        }

        public void Update(ReadOnlySpan<byte> input)
        {
            if (_hash is not null)
            {
                ThrowHashingComplete();
            }

            // If the size is zero, quit
            if (input.Length == 0)
            {
                return;
            }

            // If our provided state is empty, initialize a new one
            ulong[] state = _state;
            if (state.Length == 0)
            {
                _state = state = Pool.RentState();
            }

            // If our remainder is non zero.
            if (_remainderLength != 0)
            {
                // Copy data to our remainder
                ReadOnlySpan<byte> remainderAdditive = input[..Math.Min(input.Length, _roundSize - _remainderLength)];
                remainderAdditive.CopyTo(_remainderBuffer.AsSpan(_remainderLength));

                // Increment the length
                _remainderLength += remainderAdditive.Length;

                // Increment the input
                input = input[remainderAdditive.Length..];

                // If our remainder length equals a full round
                if (_remainderLength == _roundSize)
                {
                    // Cast our input to ulongs.
                    Span<ulong> remainderBufferU64 = MemoryMarshal.Cast<byte, ulong>(_remainderBuffer.AsSpan(0, _roundSize));

                    // Eliminate bounds check for state for the loop
                    _ = state[remainderBufferU64.Length];
                    // Loop for each ulong in this remainder, and xor the state with the input.
                    for (int i = 0; i < remainderBufferU64.Length; i++)
                    {
                        state[i] ^= remainderBufferU64[i];
                    }

                    // Perform our KeccakF on our state.
                    KeccakF(state);

                    // Clear remainder fields
                    _remainderLength = 0;
                    Pool.ReturnRemainder(ref _remainderBuffer);
                }
            }

            // Loop for every round in our size.
            while (input.Length >= _roundSize)
            {
                // Cast our input to ulongs.
                ReadOnlySpan<ulong> input64 = MemoryMarshal.Cast<byte, ulong>(input[.._roundSize]);

                // Eliminate bounds check for state for the loop
                _ = state[input64.Length];
                // Loop for each ulong in this round, and xor the state with the input.
                for (int i = 0; i < input64.Length; i++)
                {
                    state[i] ^= input64[i];
                }

                // Perform our KeccakF on our state.
                KeccakF(state);

                // Remove the input data processed this round.
                input = input[_roundSize..];
            }

            // last block and padding
            if (input.Length >= TEMP_BUFF_SIZE || input.Length > _roundSize || _roundSize + 1 >= TEMP_BUFF_SIZE || _roundSize == 0 || _roundSize - 1 >= TEMP_BUFF_SIZE)
            {
                ThrowBadKeccak();
            }

            // If we have any remainder here, it means any remainder was processed before, we can copy our data over and set our length
            if (input.Length > 0)
            {
                if (_remainderBuffer.Length == 0)
                {
                    _remainderBuffer = Pool.RentRemainder();
                }
                input.CopyTo(_remainderBuffer);
                _remainderLength = input.Length;
            }
        }

        private byte[] GenerateHash()
        {
            byte[] output = new byte[HashSize];
            UpdateFinalTo(output);
            // Obtain the state data in the desired (hash) size we want.
            _hash = output;

            // Return the result.
            return output;
        }

        public void UpdateFinalTo(Span<byte> output)
        {
            if (_hash is not null)
            {
                ThrowHashingComplete();
            }

            if (_remainderLength > 0)
            {
                Span<byte> remainder = _remainderBuffer.AsSpan(0, _roundSize);
                // Set a 1 byte after the remainder.
                remainder[_remainderLength++] = 1;

                // Set the highest bit on the last byte.
                remainder[_roundSize - 1] |= 0x80;

                // Cast the remainder buffer to ulongs.
                Span<ulong> temp64 = MemoryMarshal.Cast<byte, ulong>(remainder);
                // Loop for each ulong in this round, and xor the state with the input.
                for (int i = 0; i < temp64.Length; i++)
                {
                    _state[i] ^= temp64[i];
                }

                Pool.ReturnRemainder(ref _remainderBuffer);
            }
            else
            {
                Span<byte> temp = MemoryMarshal.AsBytes<ulong>(_state);
                // Xor 1 byte as first byte.
                temp[0] ^= 1;
                // Xor the highest bit on the last byte.
                temp[_roundSize - 1] ^= 0x80;
            }

            KeccakF(_state);

            // Obtain the state data in the desired (hash) size we want.
            MemoryMarshal.AsBytes<ulong>(_state)[..HashSize].CopyTo(output);

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

        [DoesNotReturn]
        private static void ThrowBadKeccak() => throw new ArgumentException("Bad Keccak use");

        [DoesNotReturn]
        private static void ThrowHashingComplete() => throw new InvalidOperationException("Keccak hash is complete");

        private static class Pool
        {
            private const int MaxPooledPerThread = 4;
            [ThreadStatic]
            private static Queue<byte[]>? s_remainderCache;
            public static byte[] RentRemainder() => s_remainderCache?.TryDequeue(out byte[]? remainder) ?? false ? remainder : new byte[STATE_SIZE];
            public static void ReturnRemainder(ref byte[] remainder)
            {
                if (remainder.Length == 0) return;

                var cache = (s_remainderCache ??= new());
                if (cache.Count <= MaxPooledPerThread)
                {
                    remainder.AsSpan().Clear();
                    cache.Enqueue(remainder);
                }

                remainder = Array.Empty<byte>();
            }

            [ThreadStatic]
            private static Queue<ulong[]>? s_stateCache;
            public static ulong[] RentState() => s_stateCache?.TryDequeue(out ulong[]? state) ?? false ? state : new ulong[STATE_SIZE / sizeof(ulong)];
            public static void ReturnState(ref ulong[] state)
            {
                if (state.Length == 0) return;

                var cache = (s_stateCache ??= new());
                if (cache.Count <= MaxPooledPerThread)
                {
                    state.AsSpan().Clear();
                    cache.Enqueue(state);
                }

                state = Array.Empty<ulong>();
            }
        }

        [SkipLocalsInit]
        public static void KeccakF1600Avx512F(Span<ulong> state)
        {
            {
                // Redundant statement that removes all the in loop bounds checks
                _ = state[24];
            }

            // Can straight load and over-read for start elements
            Vector512<ulong> mask = Vector512.Create(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 0UL, 0UL, 0UL);
            Vector512<ulong> c0 = Unsafe.As<ulong, Vector512<ulong>>(ref MemoryMarshal.GetReference(state));
            // Clear the over-read values from first vectors
            c0 = Vector512.BitwiseAnd(mask, c0);
            Vector512<ulong> c1 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 5));
            c1 = Vector512.BitwiseAnd(mask, c1);
            Vector512<ulong> c2 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 10));
            c2 = Vector512.BitwiseAnd(mask, c2);
            Vector512<ulong> c3 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 15));
            c3 = Vector512.BitwiseAnd(mask, c3);

            // Can't over-read for the last elements (8 items in vector 5 to be remaining)
            // so read a Vector256 and ulong then combine
            Vector256<ulong> c4a = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 20));
            Vector256<ulong> c4b = Vector256.Create(state[24], 0UL, 0UL, 0UL);
            Vector512<ulong> c4 = Vector512.Create(c4a, c4b);

            ulong[] roundConstants = RoundConstants;
            for (int round = 0; round < roundConstants.Length; round++)
            {
                // Theta step
                Vector512<ulong> bVec = Vector512.Xor(Vector512.Xor(Vector512.Xor(c0, c1), Vector512.Xor(c2, c3)), c4);

                // Compute Theta Vector
                Vector512<ulong> bVecRot1 = Avx512F.PermuteVar8x64(bVec, Vector512.Create(1UL, 2UL, 3UL, 4UL, 0UL, 5UL, 6UL, 7UL));
                Vector512<ulong> bVecRot4 = Avx512F.PermuteVar8x64(bVec, Vector512.Create(4UL, 0UL, 1UL, 2UL, 3UL, 5UL, 6UL, 7UL));

                // Rotate bVecRot1 left by 1
                Vector512<ulong> bVecRot1ShiftedLeft = Avx512F.ShiftLeftLogical(bVecRot1, 1);
                Vector512<ulong> bVecRot1ShiftedRight = Avx512F.ShiftRightLogical(bVecRot1, 63);
                Vector512<ulong> bVecRot1Rotated = Avx512F.Or(bVecRot1ShiftedLeft, bVecRot1ShiftedRight);

                Vector512<ulong> thetaVec = Avx512F.Xor(bVecRot4, bVecRot1Rotated);

                c0 = Avx512F.Xor(c0, thetaVec);
                c1 = Avx512F.Xor(c1, thetaVec);
                c2 = Avx512F.Xor(c2, thetaVec);
                c3 = Avx512F.Xor(c3, thetaVec);
                c4 = Avx512F.Xor(c4, thetaVec);

                // Rho step
                Vector512<ulong> rhoVec0 = Vector512.Create(0UL, 1UL, 62UL, 28UL, 27UL, 0UL, 0UL, 0UL);
                c0 = Avx512F.RotateLeftVariable(c0, rhoVec0);

                Vector512<ulong> rhoVec1 = Vector512.Create(36UL, 44UL, 6UL, 55UL, 20UL, 0UL, 0UL, 0UL);
                c1 = Avx512F.RotateLeftVariable(c1, rhoVec1);

                Vector512<ulong> rhoVec2 = Vector512.Create(3UL, 10UL, 43UL, 25UL, 39UL, 0UL, 0UL, 0UL);
                c2 = Avx512F.RotateLeftVariable(c2, rhoVec2);

                Vector512<ulong> rhoVec3 = Vector512.Create(41UL, 45UL, 15UL, 21UL, 8UL, 0UL, 0UL, 0UL);
                c3 = Avx512F.RotateLeftVariable(c3, rhoVec3);

                Vector512<ulong> rhoVec4 = Vector512.Create(18UL, 2UL, 61UL, 56UL, 14UL, 0UL, 0UL, 0UL);
                c4 = Avx512F.RotateLeftVariable(c4, rhoVec4);

                // Pi step
                Vector512<ulong> c0Pi = Avx512F.PermuteVar8x64x2(c0, Vector512.Create(0UL, 8 + 1, 2, 3, 4, 5, 6, 7), c1);
                c0Pi = Avx512F.PermuteVar8x64x2(c0Pi, Vector512.Create(0UL, 1, 8 + 2, 3, 4, 5, 6, 7), c2);
                c0Pi = Avx512F.PermuteVar8x64x2(c0Pi, Vector512.Create(0UL, 1, 2, 8 + 3, 4, 5, 6, 7), c3);
                c0Pi = Avx512F.PermuteVar8x64x2(c0Pi, Vector512.Create(0UL, 1, 2, 3, 8 + 4, 5, 6, 7), c4);

                Vector512<ulong> c1Pi = Avx512F.PermuteVar8x64x2(c0, Vector512.Create(3UL, 8 + 4, 2, 3, 4, 5, 6, 7), c1);
                c1Pi = Avx512F.PermuteVar8x64x2(c1Pi, Vector512.Create(0UL, 1, 8 + 0, 3, 4, 5, 6, 7), c2);
                c1Pi = Avx512F.PermuteVar8x64x2(c1Pi, Vector512.Create(0UL, 1, 2, 8 + 1, 4, 5, 6, 7), c3);
                c1Pi = Avx512F.PermuteVar8x64x2(c1Pi, Vector512.Create(0UL, 1, 2, 3, 8 + 2, 5, 6, 7), c4);

                Vector512<ulong> c2Pi = Avx512F.PermuteVar8x64x2(c0, Vector512.Create(1UL, 8 + 2, 2, 3, 4, 5, 6, 7), c1);
                c2Pi = Avx512F.PermuteVar8x64x2(c2Pi, Vector512.Create(0UL, 1, 8 + 3, 3, 4, 5, 6, 7), c2);
                c2Pi = Avx512F.PermuteVar8x64x2(c2Pi, Vector512.Create(0UL, 1, 2, 8 + 4, 4, 5, 6, 7), c3);
                c2Pi = Avx512F.PermuteVar8x64x2(c2Pi, Vector512.Create(0UL, 1, 2, 3, 8 + 0, 5, 6, 7), c4);

                Vector512<ulong> c3Pi = Avx512F.PermuteVar8x64x2(c0, Vector512.Create(4UL, 8 + 0, 2, 3, 4, 5, 6, 7), c1);
                c3Pi = Avx512F.PermuteVar8x64x2(c3Pi, Vector512.Create(0UL, 1, 8 + 1, 3, 4, 5, 6, 7), c2);
                c3Pi = Avx512F.PermuteVar8x64x2(c3Pi, Vector512.Create(0UL, 1, 2, 8 + 2, 4, 5, 6, 7), c3);
                c3Pi = Avx512F.PermuteVar8x64x2(c3Pi, Vector512.Create(0UL, 1, 2, 3, 8 + 3, 5, 6, 7), c4);

                Vector512<ulong> c4Pi = Avx512F.PermuteVar8x64x2(c0, Vector512.Create(2UL, 8 + 3, 2, 3, 4, 5, 6, 7), c1);
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 8 + 4, 3, 4, 5, 6, 7), c2);
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 2, 8 + 0, 4, 5, 6, 7), c3);
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 2, 3, 8 + 1, 5, 6, 7), c4);

                c0 = c0Pi;
                c1 = c1Pi;
                c2 = c2Pi;
                c3 = c3Pi;
                c4 = c4Pi;

                // Chi step
                Vector512<ulong> permute1 = Vector512.Create(1UL, 2UL, 3UL, 4UL, 0UL, 5UL, 6UL, 7UL);
                Vector512<ulong> permute2 = Vector512.Create(2UL, 3UL, 4UL, 0UL, 1UL, 5UL, 6UL, 7UL);

                c0 = Avx512F.TernaryLogic(c0, Avx512F.PermuteVar8x64(c0, permute1), Avx512F.PermuteVar8x64(c0, permute2), 0xD2);
                c1 = Avx512F.TernaryLogic(c1, Avx512F.PermuteVar8x64(c1, permute1), Avx512F.PermuteVar8x64(c1, permute2), 0xD2);
                c2 = Avx512F.TernaryLogic(c2, Avx512F.PermuteVar8x64(c2, permute1), Avx512F.PermuteVar8x64(c2, permute2), 0xD2);
                c3 = Avx512F.TernaryLogic(c3, Avx512F.PermuteVar8x64(c3, permute1), Avx512F.PermuteVar8x64(c3, permute2), 0xD2);
                c4 = Avx512F.TernaryLogic(c4, Avx512F.PermuteVar8x64(c4, permute1), Avx512F.PermuteVar8x64(c4, permute2), 0xD2);

                // Iota step
                c0 = Vector512.Xor(c0, Vector512.Create(roundConstants[round], 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL));
            }

            // Can over-write for first elements
            Unsafe.As<ulong, Vector512<ulong>>(ref MemoryMarshal.GetReference(state)) = c0;
            Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 5)) = c1;
            Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 10)) = c2;
            Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 15)) = c3;
            // Can't over-write for last elements so write the upper Vector256 and then ulong
            Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 20)) = c3.GetUpper();
            state[24] = c4.GetElement(4);
        }
    }
}
