// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

            ulong bCa, bCe, bCi, bCo, bCu;
            ulong da, de, di, @do, du;
            ulong eba, ebe, ebi, ebo, ebu;
            ulong ega, ege, egi, ego, egu;
            ulong eka, eke, eki, eko, eku;
            ulong ema, eme, emi, emo, emu;
            ulong esa, ese, esi, eso, esu;

            Span<ulong> asa_ase_asi_aso_asu = st.Slice(20, 5);
            Span<ulong> ama_ame_ami_amo_amu = st.Slice(15, 5);
            Span<ulong> aka_ake_aki_ako_aku = st.Slice(10, 5);
            Span<ulong> aga_age_agi_ago_agu = st.Slice(5, 5);
            Span<ulong> aba_abe_abi_abo_abu = st.Slice(0, 5);

            for (int round = 0; round < ROUNDS; round += 2)
            {
                //    prepareTheta
                bCa = aba_abe_abi_abo_abu[0] ^ aga_age_agi_ago_agu[0] ^ aka_ake_aki_ako_aku[0] ^ ama_ame_ami_amo_amu[0] ^ asa_ase_asi_aso_asu[0];
                bCe = aba_abe_abi_abo_abu[1] ^ aga_age_agi_ago_agu[1] ^ aka_ake_aki_ako_aku[1] ^ ama_ame_ami_amo_amu[1] ^ asa_ase_asi_aso_asu[1];
                bCi = aba_abe_abi_abo_abu[2] ^ aga_age_agi_ago_agu[2] ^ aka_ake_aki_ako_aku[2] ^ ama_ame_ami_amo_amu[2] ^ asa_ase_asi_aso_asu[2];
                bCo = aba_abe_abi_abo_abu[3] ^ aga_age_agi_ago_agu[3] ^ aka_ake_aki_ako_aku[3] ^ ama_ame_ami_amo_amu[3] ^ asa_ase_asi_aso_asu[3];
                bCu = aba_abe_abi_abo_abu[4] ^ aga_age_agi_ago_agu[4] ^ aka_ake_aki_ako_aku[4] ^ ama_ame_ami_amo_amu[4] ^ asa_ase_asi_aso_asu[4];

                //thetaRhoPiChiIotaPrepareTheta(round  , A, E)
                da = bCu ^ RotateLeft(bCe, 1);
                de = bCa ^ RotateLeft(bCi, 1);
                di = bCe ^ RotateLeft(bCo, 1);
                @do = bCi ^ RotateLeft(bCu, 1);
                du = bCo ^ RotateLeft(bCa, 1);

                aba_abe_abi_abo_abu[0] ^= da;
                aga_age_agi_ago_agu[1] ^= de;
                aka_ake_aki_ako_aku[2] ^= di;
                ama_ame_ami_amo_amu[3] ^= @do;
                asa_ase_asi_aso_asu[4] ^= du;

                bCa = aba_abe_abi_abo_abu[0];
                bCe = RotateLeft(aga_age_agi_ago_agu[1], 44);
                bCi = RotateLeft(aka_ake_aki_ako_aku[2], 43);
                bCo = RotateLeft(ama_ame_ami_amo_amu[3], 21);
                bCu = RotateLeft(asa_ase_asi_aso_asu[4], 14);

                eba = bCa ^ ((~bCe) & bCi);
                eba ^= RoundConstants[round];
                ebe = bCe ^ ((~bCi) & bCo);
                ebi = bCi ^ ((~bCo) & bCu);
                ebo = bCo ^ ((~bCu) & bCa);
                ebu = bCu ^ ((~bCa) & bCe);

                aba_abe_abi_abo_abu[3] ^= @do;
                aga_age_agi_ago_agu[4] ^= du;
                aka_ake_aki_ako_aku[0] ^= da;
                ama_ame_ami_amo_amu[1] ^= de;
                asa_ase_asi_aso_asu[2] ^= di;

                bCa = RotateLeft(aba_abe_abi_abo_abu[3], 28);
                bCe = RotateLeft(aga_age_agi_ago_agu[4], 20);
                bCi = RotateLeft(aka_ake_aki_ako_aku[0], 3);
                bCo = RotateLeft(ama_ame_ami_amo_amu[1], 45);
                bCu = RotateLeft(asa_ase_asi_aso_asu[2], 61);

                ega = bCa ^ ((~bCe) & bCi);
                ege = bCe ^ ((~bCi) & bCo);
                egi = bCi ^ ((~bCo) & bCu);
                ego = bCo ^ ((~bCu) & bCa);
                egu = bCu ^ ((~bCa) & bCe);

                aba_abe_abi_abo_abu[1] ^= de;
                aga_age_agi_ago_agu[2] ^= di;
                aka_ake_aki_ako_aku[3] ^= @do;
                ama_ame_ami_amo_amu[4] ^= du;
                asa_ase_asi_aso_asu[0] ^= da;

                bCa = RotateLeft(aba_abe_abi_abo_abu[1], 1);
                bCe = RotateLeft(aga_age_agi_ago_agu[2], 6);
                bCi = RotateLeft(aka_ake_aki_ako_aku[3], 25);
                bCo = RotateLeft(ama_ame_ami_amo_amu[4], 8);
                bCu = RotateLeft(asa_ase_asi_aso_asu[0], 18);

                eka = bCa ^ ((~bCe) & bCi);
                eke = bCe ^ ((~bCi) & bCo);
                eki = bCi ^ ((~bCo) & bCu);
                eko = bCo ^ ((~bCu) & bCa);
                eku = bCu ^ ((~bCa) & bCe);

                aba_abe_abi_abo_abu[4] ^= du;
                aga_age_agi_ago_agu[0] ^= da;
                aka_ake_aki_ako_aku[1] ^= de;
                ama_ame_ami_amo_amu[2] ^= di;
                asa_ase_asi_aso_asu[3] ^= @do;

                bCa = RotateLeft(aba_abe_abi_abo_abu[4], 27);
                bCe = RotateLeft(aga_age_agi_ago_agu[0], 36);
                bCi = RotateLeft(aka_ake_aki_ako_aku[1], 10);
                bCo = RotateLeft(ama_ame_ami_amo_amu[2], 15);
                bCu = RotateLeft(asa_ase_asi_aso_asu[3], 56);

                ema = bCa ^ ((~bCe) & bCi);
                eme = bCe ^ ((~bCi) & bCo);
                emi = bCi ^ ((~bCo) & bCu);
                emo = bCo ^ ((~bCu) & bCa);
                emu = bCu ^ ((~bCa) & bCe);

                aba_abe_abi_abo_abu[2] ^= di;
                aga_age_agi_ago_agu[3] ^= @do;
                aka_ake_aki_ako_aku[4] ^= du;
                ama_ame_ami_amo_amu[0] ^= da;
                asa_ase_asi_aso_asu[1] ^= de;

                bCa = RotateLeft(aba_abe_abi_abo_abu[2], 62);
                bCe = RotateLeft(aga_age_agi_ago_agu[3], 55);
                bCi = RotateLeft(aka_ake_aki_ako_aku[4], 39);
                bCo = RotateLeft(ama_ame_ami_amo_amu[0], 41);
                bCu = RotateLeft(asa_ase_asi_aso_asu[1], 2);

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

                bCa = eba ^ da;
                bCe = RotateLeft(ege ^ de, 44);
                bCo = RotateLeft(emo ^ @do, 21);
                bCi = RotateLeft(eki ^ di, 43);
                bCu = RotateLeft(esu ^ du, 14);

                aba_abe_abi_abo_abu[0] = bCa ^ ((~bCe) & bCi);
                aba_abe_abi_abo_abu[0] ^= RoundConstants[round + 1];
                aba_abe_abi_abo_abu[1] = bCe ^ ((~bCi) & bCo);
                aba_abe_abi_abo_abu[2] = bCi ^ ((~bCo) & bCu);
                aba_abe_abi_abo_abu[3] = bCo ^ ((~bCu) & bCa);
                aba_abe_abi_abo_abu[4] = bCu ^ ((~bCa) & bCe);


                bCa = RotateLeft(ebo ^ @do, 28);
                bCe = RotateLeft(egu ^ du, 20);
                bCi = RotateLeft(eka ^ da, 3);
                bCo = RotateLeft(eme ^ de, 45);
                bCu = RotateLeft(esi ^ di, 61);

                aga_age_agi_ago_agu[0] = bCa ^ ((~bCe) & bCi);
                aga_age_agi_ago_agu[1] = bCe ^ ((~bCi) & bCo);
                aga_age_agi_ago_agu[2] = bCi ^ ((~bCo) & bCu);
                aga_age_agi_ago_agu[3] = bCo ^ ((~bCu) & bCa);
                aga_age_agi_ago_agu[4] = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(ebe ^ de, 1);
                bCe = RotateLeft(egi ^ di, 6);
                bCi = RotateLeft(eko ^ @do, 25);
                bCo = RotateLeft(emu ^ du, 8);
                bCu = RotateLeft(esa ^ da, 18);

                aka_ake_aki_ako_aku[0] = bCa ^ ((~bCe) & bCi);
                aka_ake_aki_ako_aku[1] = bCe ^ ((~bCi) & bCo);
                aka_ake_aki_ako_aku[2] = bCi ^ ((~bCo) & bCu);
                aka_ake_aki_ako_aku[3] = bCo ^ ((~bCu) & bCa);
                aka_ake_aki_ako_aku[4] = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(ebu ^ du, 27);
                bCe = RotateLeft(ega ^ da, 36);
                bCi = RotateLeft(eke ^ de, 10);
                bCo = RotateLeft(emi ^ di, 15);
                bCu = RotateLeft(eso ^ @do, 56);
                ama_ame_ami_amo_amu[0] = bCa ^ ((~bCe) & bCi);
                ama_ame_ami_amo_amu[1] = bCe ^ ((~bCi) & bCo);
                ama_ame_ami_amo_amu[2] = bCi ^ ((~bCo) & bCu);
                ama_ame_ami_amo_amu[3] = bCo ^ ((~bCu) & bCa);
                ama_ame_ami_amo_amu[4] = bCu ^ ((~bCa) & bCe);

                bCa = RotateLeft(ebi ^ di, 62);
                bCe = RotateLeft(ego ^ @do, 55);
                bCi = RotateLeft(eku ^ du, 39);
                bCo = RotateLeft(ema ^ da, 41);
                bCu = RotateLeft(ese ^ de, 2);
                asa_ase_asi_aso_asu[0] = bCa ^ ((~bCe) & bCi);
                asa_ase_asi_aso_asu[1] = bCe ^ ((~bCi) & bCo);
                asa_ase_asi_aso_asu[2] = bCi ^ ((~bCo) & bCu);
                asa_ase_asi_aso_asu[3] = bCo ^ ((~bCu) & bCa);
                asa_ase_asi_aso_asu[4] = bCu ^ ((~bCa) & bCe);
            }
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
    }
}
