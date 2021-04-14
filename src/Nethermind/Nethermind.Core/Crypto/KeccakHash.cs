//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// ReSharper disable InconsistentNaming
namespace Nethermind.Core.Crypto
{
    public class KeccakHash
    {
        public const int HASH_SIZE = 32;
        private const int STATE_SIZE = 200;
        private const int ROUND_SIZE = STATE_SIZE - 2 * HASH_SIZE;
        private const int ROUND_SIZE_U64 = ROUND_SIZE / 8;
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

        private int _roundSize;
        private int _roundSizeU64;
        private Memory<byte> _remainderBuffer;
        private int _remainderLength;
        private Memory<ulong> _state;
        private byte[]? _hash;

        /// <summary>
        /// Indicates the hash size in bytes.
        /// </summary>
        public int HashSize { get; }

        /// <summary>
        /// The current hash buffer at this point. Recomputed after hash updates.
        /// </summary>
        public byte[] Hash
        {
            get
            {
                // If the hash is null, recalculate.
                return _hash ??= UpdateFinal();
            }
        }

        #region Constructor

        private KeccakHash(int size)
        {
            // Set the hash size
            HashSize = size;

            // Verify the size
            if (HashSize <= 0 || HashSize > STATE_SIZE)
            {
                throw new ArgumentException($"Invalid Keccak hash size. Must be between 0 and {STATE_SIZE}.");
            }

            // The round size.
            _roundSize = STATE_SIZE == HashSize ? HASH_DATA_AREA : STATE_SIZE - (2 * HashSize);

            // The size of a round in terms of ulong.
            _roundSizeU64 = _roundSize / 8;

            // Allocate our remainder buffer
            _remainderBuffer = new byte[_roundSize];
            _remainderLength = 0;
        }

        #endregion

        #region Functions

        public static KeccakHash Create(int size = HASH_SIZE)
        {
            return new(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ROL(ulong a, int offset)
        {
            return (a << (offset % LANE_BITS)) ^ (a >> (LANE_BITS - (offset % LANE_BITS)));
        }

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

            //copyFromState(A, state)
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

            for (var round = 0; round < ROUNDS; round += 2)
            {
                //    prepareTheta
                bCa = aba ^ aga ^ aka ^ ama ^ asa;
                bCe = abe ^ age ^ ake ^ ame ^ ase;
                bCi = abi ^ agi ^ aki ^ ami ^ asi;
                bCo = abo ^ ago ^ ako ^ amo ^ aso;
                bCu = abu ^ agu ^ aku ^ amu ^ asu;

                //thetaRhoPiChiIotaPrepareTheta(round  , A, E)
                da = bCu ^ ROL(bCe, 1);
                de = bCa ^ ROL(bCi, 1);
                di = bCe ^ ROL(bCo, 1);
                @do = bCi ^ ROL(bCu, 1);
                du = bCo ^ ROL(bCa, 1);

                aba ^= da;
                bCa = aba;
                age ^= de;
                bCe = ROL(age, 44);
                aki ^= di;
                bCi = ROL(aki, 43);
                amo ^= @do;
                bCo = ROL(amo, 21);
                asu ^= du;
                bCu = ROL(asu, 14);
                eba = bCa ^ ((~bCe) & bCi);
                eba ^= RoundConstants[round];
                ebe = bCe ^ ((~bCi) & bCo);
                ebi = bCi ^ ((~bCo) & bCu);
                ebo = bCo ^ ((~bCu) & bCa);
                ebu = bCu ^ ((~bCa) & bCe);

                abo ^= @do;
                bCa = ROL(abo, 28);
                agu ^= du;
                bCe = ROL(agu, 20);
                aka ^= da;
                bCi = ROL(aka, 3);
                ame ^= de;
                bCo = ROL(ame, 45);
                asi ^= di;
                bCu = ROL(asi, 61);
                ega = bCa ^ ((~bCe) & bCi);
                ege = bCe ^ ((~bCi) & bCo);
                egi = bCi ^ ((~bCo) & bCu);
                ego = bCo ^ ((~bCu) & bCa);
                egu = bCu ^ ((~bCa) & bCe);

                abe ^= de;
                bCa = ROL(abe, 1);
                agi ^= di;
                bCe = ROL(agi, 6);
                ako ^= @do;
                bCi = ROL(ako, 25);
                amu ^= du;
                bCo = ROL(amu, 8);
                asa ^= da;
                bCu = ROL(asa, 18);
                eka = bCa ^ ((~bCe) & bCi);
                eke = bCe ^ ((~bCi) & bCo);
                eki = bCi ^ ((~bCo) & bCu);
                eko = bCo ^ ((~bCu) & bCa);
                eku = bCu ^ ((~bCa) & bCe);

                abu ^= du;
                bCa = ROL(abu, 27);
                aga ^= da;
                bCe = ROL(aga, 36);
                ake ^= de;
                bCi = ROL(ake, 10);
                ami ^= di;
                bCo = ROL(ami, 15);
                aso ^= @do;
                bCu = ROL(aso, 56);
                ema = bCa ^ ((~bCe) & bCi);
                eme = bCe ^ ((~bCi) & bCo);
                emi = bCi ^ ((~bCo) & bCu);
                emo = bCo ^ ((~bCu) & bCa);
                emu = bCu ^ ((~bCa) & bCe);

                abi ^= di;
                bCa = ROL(abi, 62);
                ago ^= @do;
                bCe = ROL(ago, 55);
                aku ^= du;
                bCi = ROL(aku, 39);
                ama ^= da;
                bCo = ROL(ama, 41);
                ase ^= de;
                bCu = ROL(ase, 2);
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
                da = bCu ^ ROL(bCe, 1);
                de = bCa ^ ROL(bCi, 1);
                di = bCe ^ ROL(bCo, 1);
                @do = bCi ^ ROL(bCu, 1);
                du = bCo ^ ROL(bCa, 1);

                eba ^= da;
                bCa = eba;
                ege ^= de;
                bCe = ROL(ege, 44);
                eki ^= di;
                bCi = ROL(eki, 43);
                emo ^= @do;
                bCo = ROL(emo, 21);
                esu ^= du;
                bCu = ROL(esu, 14);
                aba = bCa ^ ((~bCe) & bCi);
                aba ^= RoundConstants[round + 1];
                abe = bCe ^ ((~bCi) & bCo);
                abi = bCi ^ ((~bCo) & bCu);
                abo = bCo ^ ((~bCu) & bCa);
                abu = bCu ^ ((~bCa) & bCe);

                ebo ^= @do;
                bCa = ROL(ebo, 28);
                egu ^= du;
                bCe = ROL(egu, 20);
                eka ^= da;
                bCi = ROL(eka, 3);
                eme ^= de;
                bCo = ROL(eme, 45);
                esi ^= di;
                bCu = ROL(esi, 61);
                aga = bCa ^ ((~bCe) & bCi);
                age = bCe ^ ((~bCi) & bCo);
                agi = bCi ^ ((~bCo) & bCu);
                ago = bCo ^ ((~bCu) & bCa);
                agu = bCu ^ ((~bCa) & bCe);

                ebe ^= de;
                bCa = ROL(ebe, 1);
                egi ^= di;
                bCe = ROL(egi, 6);
                eko ^= @do;
                bCi = ROL(eko, 25);
                emu ^= du;
                bCo = ROL(emu, 8);
                esa ^= da;
                bCu = ROL(esa, 18);
                aka = bCa ^ ((~bCe) & bCi);
                ake = bCe ^ ((~bCi) & bCo);
                aki = bCi ^ ((~bCo) & bCu);
                ako = bCo ^ ((~bCu) & bCa);
                aku = bCu ^ ((~bCa) & bCe);

                ebu ^= du;
                bCa = ROL(ebu, 27);
                ega ^= da;
                bCe = ROL(ega, 36);
                eke ^= de;
                bCi = ROL(eke, 10);
                emi ^= di;
                bCo = ROL(emi, 15);
                eso ^= @do;
                bCu = ROL(eso, 56);
                ama = bCa ^ ((~bCe) & bCi);
                ame = bCe ^ ((~bCi) & bCo);
                ami = bCi ^ ((~bCo) & bCu);
                amo = bCo ^ ((~bCu) & bCa);
                amu = bCu ^ ((~bCa) & bCe);

                ebi ^= di;
                bCa = ROL(ebi, 62);
                ego ^= @do;
                bCe = ROL(ego, 55);
                eku ^= du;
                bCi = ROL(eku, 39);
                ema ^= da;
                bCo = ROL(ema, 41);
                ese ^= de;
                bCu = ROL(ese, 2);
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
            var output = new byte[HASH_SIZE];
            ComputeHash(input, output);
            return output;
        }
        
        public static void ComputeHashBytesToSpan(ReadOnlySpan<byte> input, Span<byte> output, int size = HASH_SIZE)
        {
            ComputeHash(input, output);
        }        

        // compute a keccak hash (md) of given byte length from "in"
        public static void ComputeHash(ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (output.Length <= 0 || output.Length > STATE_SIZE)
            {
                throw new ArgumentException("Bad keccak use");
            }
            
            Span<ulong> state = stackalloc ulong[STATE_SIZE / sizeof(ulong)];
            Span<byte> temp = stackalloc byte[TEMP_BUFF_SIZE];

            var remainingInputLength = input.Length;
            int i;
            for (; remainingInputLength >= ROUND_SIZE; remainingInputLength -= ROUND_SIZE, input = input.Slice(ROUND_SIZE))
            {
                var input64 = MemoryMarshal.Cast<byte, ulong>(input);

                for (i = 0; i < ROUND_SIZE_U64; i++)
                {
                    state[i] ^= input64[i];
                }

                KeccakF(state);
            }

            // last block and padding
            if (input.Length >= TEMP_BUFF_SIZE || input.Length > ROUND_SIZE || ROUND_SIZE + 1 >= TEMP_BUFF_SIZE || ROUND_SIZE == 0 || ROUND_SIZE - 1 >= TEMP_BUFF_SIZE || ROUND_SIZE_U64 * 8 > TEMP_BUFF_SIZE)
            {
                throw new ArgumentException("Bad keccak use");
            }

            input.Slice(0, remainingInputLength).CopyTo(temp);
            temp[remainingInputLength] = 1;
            temp[ROUND_SIZE - 1] |= 0x80;

            var tempU64 = MemoryMarshal.Cast<byte, ulong>(temp);

            for (i = 0; i < ROUND_SIZE_U64; i++)
            {
                state[i] ^= tempU64[i];
            }

            KeccakF(state);
            MemoryMarshal.AsBytes(state.Slice(0, HASH_SIZE / sizeof(ulong))).CopyTo(output);
        }
        
        public void Update(Span<byte> array, int index, int size)
        {
            // Bounds checking.
            if (size < 0)
            {
                throw new ArgumentException("Cannot updated Keccak hash because the provided size of data to hash is negative.");
            }
            else if (index + size > array.Length || index < 0)
            {
                throw new ArgumentOutOfRangeException("Cannot updated Keccak hash because the provided index and size extend outside the bounds of the array.");
            }

            // If the size is zero, quit
            if (size == 0)
            {
                return;
            }

            // Create the input buffer
            Span<byte> input = array;
            input = input.Slice(index, size);

            // If our provided state is empty, initialize a new one
            if (_state.Length == 0)
            {
                _state = new ulong[STATE_SIZE / 8];
            }

            // If our remainder is non zero.
            int i;
            if (_remainderLength != 0)
            {
                // Copy data to our remainder
                var remainderAdditive = input.Slice(0, Math.Min(input.Length, _roundSize - _remainderLength));
                remainderAdditive.CopyTo(_remainderBuffer.Slice(_remainderLength).Span);

                // Increment the length
                _remainderLength += remainderAdditive.Length;

                // Increment the input
                input = input.Slice(remainderAdditive.Length);

                // If our remainder length equals a full round
                if (_remainderLength == _roundSize)
                {
                    // Cast our input to ulongs.
                    var remainderBufferU64 = MemoryMarshal.Cast<byte, ulong>(_remainderBuffer.Span);

                    // Loop for each ulong in this remainder, and xor the state with the input.
                    for (i = 0; i < _roundSizeU64; i++)
                    {
                        _state.Span[i] ^= remainderBufferU64[i];
                    }

                    // Perform our keccakF on our state.
                    KeccakF(_state.Span);

                    // Clear remainder fields
                    _remainderLength = 0;
                    _remainderBuffer.Span.Clear();
                }
            }

            // Loop for every round in our size.
            while (input.Length >= _roundSize)
            {
                // Cast our input to ulongs.
                var input64 = MemoryMarshal.Cast<byte, ulong>(input);

                // Loop for each ulong in this round, and xor the state with the input.
                for (i = 0; i < _roundSizeU64; i++)
                {
                    _state.Span[i] ^= input64[i];
                }

                // Perform our keccakF on our state.
                KeccakF(_state.Span);

                // Remove the input data processed this round.
                input = input.Slice(_roundSize);
            }

            // last block and padding
            if (input.Length >= TEMP_BUFF_SIZE || input.Length > _roundSize || _roundSize + 1 >= TEMP_BUFF_SIZE || _roundSize == 0 || _roundSize - 1 >= TEMP_BUFF_SIZE || _roundSizeU64 * 8 > TEMP_BUFF_SIZE)
            {
                throw new ArgumentException("Bad keccak use");
            }

            // If we have any remainder here, it means any remainder was processed before, we can copy our data over and set our length
            if (input.Length > 0)
            {
                input.CopyTo(_remainderBuffer.Span);
                _remainderLength = input.Length;
            }

            // Set the hash as null
            _hash = null;
        }

        private byte[] UpdateFinal()
        {
            // Copy the remainder buffer
            Memory<byte> remainderClone = _remainderBuffer.ToArray();

            // Set a 1 byte after the remainder.
            remainderClone.Span[_remainderLength++] = 1;

            // Set the highest bit on the last byte.
            remainderClone.Span[_roundSize - 1] |= 0x80;

            // Cast the remainder buffer to ulongs.
            var temp64 = MemoryMarshal.Cast<byte, ulong>(remainderClone.Span);

            // Loop for each ulong in this round, and xor the state with the input.
            for (int i = 0; i < _roundSizeU64; i++)
            {
                _state.Span[i] ^= temp64[i];
            }

            KeccakF(_state.Span);

            // Obtain the state data in the desired (hash) size we want.
            _hash = MemoryMarshal.AsBytes(_state.Span).Slice(0, HashSize).ToArray();

            // Return the result.
            return Hash;
        }

        public void Reset()
        {
            // Clear our hash state information.
            _state.Span.Clear();
            _remainderBuffer.Span.Clear();
            _remainderLength = 0;
            _hash = null;
        }

        #endregion
    }
}
