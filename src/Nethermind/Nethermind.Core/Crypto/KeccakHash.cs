// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

            {
                // Access last element to perform range check once
                // and not for every ascending access
                _ = st[24];
            }
            aba = st[0];
            abe = st[1];
            abi = st[2];
            abo = st[3];
            abu = st[4];
            aga = st[5];
            age = st[6];
            agi = st[7];
            ago = st[8];
            agu = st[9];
            aka = st[10];
            ake = st[11];
            aki = st[12];
            ako = st[13];
            aku = st[14];
            ama = st[15];
            ame = st[16];
            ami = st[17];
            amo = st[18];
            amu = st[19];
            asa = st[20];
            ase = st[21];
            asi = st[22];
            aso = st[23];
            asu = st[24];

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

                aba ^= da;
                bCa = aba;
                age ^= de;
                bCe = RotateLeft(age, 44);
                aki ^= di;
                bCi = RotateLeft(aki, 43);
                amo ^= @do;
                bCo = RotateLeft(amo, 21);
                asu ^= du;
                bCu = RotateLeft(asu, 14);
                eba = bCa ^ ((~bCe) & bCi);
                eba ^= RoundConstants[round];
                ebe = bCe ^ ((~bCi) & bCo);
                ebi = bCi ^ ((~bCo) & bCu);
                ebo = bCo ^ ((~bCu) & bCa);
                ebu = bCu ^ ((~bCa) & bCe);

                abo ^= @do;
                bCa = RotateLeft(abo, 28);
                agu ^= du;
                bCe = RotateLeft(agu, 20);
                aka ^= da;
                bCi = RotateLeft(aka, 3);
                ame ^= de;
                bCo = RotateLeft(ame, 45);
                asi ^= di;
                bCu = RotateLeft(asi, 61);
                ega = bCa ^ ((~bCe) & bCi);
                ege = bCe ^ ((~bCi) & bCo);
                egi = bCi ^ ((~bCo) & bCu);
                ego = bCo ^ ((~bCu) & bCa);
                egu = bCu ^ ((~bCa) & bCe);

                abe ^= de;
                bCa = RotateLeft(abe, 1);
                agi ^= di;
                bCe = RotateLeft(agi, 6);
                ako ^= @do;
                bCi = RotateLeft(ako, 25);
                amu ^= du;
                bCo = RotateLeft(amu, 8);
                asa ^= da;
                bCu = RotateLeft(asa, 18);
                eka = bCa ^ ((~bCe) & bCi);
                eke = bCe ^ ((~bCi) & bCo);
                eki = bCi ^ ((~bCo) & bCu);
                eko = bCo ^ ((~bCu) & bCa);
                eku = bCu ^ ((~bCa) & bCe);

                abu ^= du;
                bCa = RotateLeft(abu, 27);
                aga ^= da;
                bCe = RotateLeft(aga, 36);
                ake ^= de;
                bCi = RotateLeft(ake, 10);
                ami ^= di;
                bCo = RotateLeft(ami, 15);
                aso ^= @do;
                bCu = RotateLeft(aso, 56);
                ema = bCa ^ ((~bCe) & bCi);
                eme = bCe ^ ((~bCi) & bCo);
                emi = bCi ^ ((~bCo) & bCu);
                emo = bCo ^ ((~bCu) & bCa);
                emu = bCu ^ ((~bCa) & bCe);

                abi ^= di;
                bCa = RotateLeft(abi, 62);
                ago ^= @do;
                bCe = RotateLeft(ago, 55);
                aku ^= du;
                bCi = RotateLeft(aku, 39);
                ama ^= da;
                bCo = RotateLeft(ama, 41);
                ase ^= de;
                bCu = RotateLeft(ase, 2);
                esa = bCa ^ ((~bCe) & bCi);
                ese = bCe ^ ((~bCi) & bCo);
                esi = bCi ^ ((~bCo) & bCu);
                eso = bCo ^ ((~bCu) & bCa);
                esu = bCu ^ ((~bCa) & bCe);

                //    prepareTheta
                bCa = eba ^ ega ^ eka ^ ema ^ esa;
                bCe = ebe ^ ege ^ eke ^ eme ^ ese;
                bCi = ebi ^ egi ^ eki ^ emi ^ esi;
                bCo = ebo ^ ego ^ eko ^ emo ^ eso;
                bCu = ebu ^ egu ^ eku ^ emu ^ esu;

                //thetaRhoPiChiIotaPrepareTheta(round+1, E, A)
                da = bCu ^ RotateLeft(bCe, 1);
                de = bCa ^ RotateLeft(bCi, 1);
                di = bCe ^ RotateLeft(bCo, 1);
                @do = bCi ^ RotateLeft(bCu, 1);
                du = bCo ^ RotateLeft(bCa, 1);

                eba ^= da;
                bCa = eba;
                ege ^= de;
                bCe = RotateLeft(ege, 44);
                eki ^= di;
                bCi = RotateLeft(eki, 43);
                emo ^= @do;
                bCo = RotateLeft(emo, 21);
                esu ^= du;
                bCu = RotateLeft(esu, 14);
                aba = bCa ^ ((~bCe) & bCi);
                aba ^= RoundConstants[round + 1];
                abe = bCe ^ ((~bCi) & bCo);
                abi = bCi ^ ((~bCo) & bCu);
                abo = bCo ^ ((~bCu) & bCa);
                abu = bCu ^ ((~bCa) & bCe);

                ebo ^= @do;
                bCa = RotateLeft(ebo, 28);
                egu ^= du;
                bCe = RotateLeft(egu, 20);
                eka ^= da;
                bCi = RotateLeft(eka, 3);
                eme ^= de;
                bCo = RotateLeft(eme, 45);
                esi ^= di;
                bCu = RotateLeft(esi, 61);
                aga = bCa ^ ((~bCe) & bCi);
                age = bCe ^ ((~bCi) & bCo);
                agi = bCi ^ ((~bCo) & bCu);
                ago = bCo ^ ((~bCu) & bCa);
                agu = bCu ^ ((~bCa) & bCe);

                ebe ^= de;
                bCa = RotateLeft(ebe, 1);
                egi ^= di;
                bCe = RotateLeft(egi, 6);
                eko ^= @do;
                bCi = RotateLeft(eko, 25);
                emu ^= du;
                bCo = RotateLeft(emu, 8);
                esa ^= da;
                bCu = RotateLeft(esa, 18);
                aka = bCa ^ ((~bCe) & bCi);
                ake = bCe ^ ((~bCi) & bCo);
                aki = bCi ^ ((~bCo) & bCu);
                ako = bCo ^ ((~bCu) & bCa);
                aku = bCu ^ ((~bCa) & bCe);

                ebu ^= du;
                bCa = RotateLeft(ebu, 27);
                ega ^= da;
                bCe = RotateLeft(ega, 36);
                eke ^= de;
                bCi = RotateLeft(eke, 10);
                emi ^= di;
                bCo = RotateLeft(emi, 15);
                eso ^= @do;
                bCu = RotateLeft(eso, 56);
                ama = bCa ^ ((~bCe) & bCi);
                ame = bCe ^ ((~bCi) & bCo);
                ami = bCi ^ ((~bCo) & bCu);
                amo = bCo ^ ((~bCu) & bCa);
                amu = bCu ^ ((~bCa) & bCe);

                ebi ^= di;
                bCa = RotateLeft(ebi, 62);
                ego ^= @do;
                bCe = RotateLeft(ego, 55);
                eku ^= du;
                bCi = RotateLeft(eku, 39);
                ema ^= da;
                bCo = RotateLeft(ema, 41);
                ese ^= de;
                bCu = RotateLeft(ese, 2);
                asa = bCa ^ ((~bCe) & bCi);
                ase = bCe ^ ((~bCi) & bCo);
                asi = bCi ^ ((~bCo) & bCu);
                aso = bCo ^ ((~bCu) & bCa);
                asu = bCu ^ ((~bCa) & bCe);
            }

            //copyToState(state, A)
            st[0] = aba;
            st[1] = abe;
            st[2] = abi;
            st[3] = abo;
            st[4] = abu;
            st[5] = aga;
            st[6] = age;
            st[7] = agi;
            st[8] = ago;
            st[9] = agu;
            st[10] = aka;
            st[11] = ake;
            st[12] = aki;
            st[13] = ako;
            st[14] = aku;
            st[15] = ama;
            st[16] = ame;
            st[17] = ami;
            st[18] = amo;
            st[19] = amu;
            st[20] = asa;
            st[21] = ase;
            st[22] = asi;
            st[23] = aso;
            st[24] = asu;
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

        public static uint[] ComputeBytesToUint(byte[] input, int size)
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
            private const int MaxPooled = 24;
            private static ConcurrentQueue<byte[]> s_remainderCache = new();
            public static byte[] RentRemainder() => s_remainderCache.TryDequeue(out byte[]? remainder) ? remainder : new byte[STATE_SIZE];
            public static void ReturnRemainder(ref byte[] remainder)
            {
                if (remainder.Length == 0) return;

                if (s_remainderCache.Count <= MaxPooled)
                {
                    remainder.AsSpan().Clear();
                    s_remainderCache.Enqueue(remainder);
                }

                remainder = Array.Empty<byte>();
            }

            private static ConcurrentQueue<ulong[]> s_stateCache = new();
            public static ulong[] RentState() => s_stateCache.TryDequeue(out ulong[]? state) ? state : new ulong[STATE_SIZE / sizeof(ulong)];
            public static void ReturnState(ref ulong[] state)
            {
                if (state.Length == 0) return;

                if (s_stateCache.Count <= MaxPooled)
                {
                    state.AsSpan().Clear();
                    s_stateCache.Enqueue(state);
                }

                state = Array.Empty<ulong>();
            }
        }
    }
}
