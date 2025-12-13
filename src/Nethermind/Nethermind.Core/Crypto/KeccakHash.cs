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

        private byte[] _remainderBuffer = [];
        private ulong[] _state = [];
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
            ComputeHash(input, MemoryMarshal.Cast<uint, byte>(output.AsSpan()));
            return output;
        }

        // compute a Keccak hash (md) of given byte length from "in"
        public static void ComputeHash(ReadOnlySpan<byte> input, Span<byte> output)
        {
            int roundSize = GetRoundSize(output.Length);
            if (output.Length <= 0 || output.Length > STATE_SIZE)
            {
                ThrowBadKeccak();
            }

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

        public static void BenchmarkHash(ReadOnlySpan<byte> input, Span<byte> output, bool useAvx512)
        {
            int roundSize = GetRoundSize(output.Length);
            if (output.Length <= 0 || output.Length > STATE_SIZE)
            {
                ThrowBadKeccak();
            }

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
                    if (useAvx512) KeccakF1600Avx512F(state); else KeccakF1600(state);
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
            if (useAvx512) KeccakF1600Avx512F(state); else KeccakF1600(state);

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void XorVectors(Span<byte> state, ReadOnlySpan<byte> input)
        {
            ref byte stateRef = ref MemoryMarshal.GetReference(state);
            if (Vector512<byte>.IsSupported && input.Length >= Vector512<byte>.Count)
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

            if (Vector256<byte>.IsSupported && input.Length >= Vector256<byte>.Count)
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

            if (Vector128<byte>.IsSupported && input.Length >= Vector128<byte>.Count)
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

            ulong[] state = _state;
            if (state.Length == 0)
            {
                // If our provided state is empty, initialize a new one
                _state = state = Pool.RentState();
            }

            int offset = 0;
            Span<byte> stateBytes = MemoryMarshal.AsBytes(state.AsSpan());

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
                    // XOR the remainder buffer into the state using XorVectors
                    XorVectors(stateBytes, _remainderBuffer);
                    KeccakF(state);

                    // Reset remainder
                    _remainderLength = 0;
                    Pool.ReturnRemainder(ref _remainderBuffer);
                }
            }

            // Process full rounds
            while (input.Length - offset >= _roundSize)
            {
                XorVectors(stateBytes, input.Slice(offset, _roundSize));
                KeccakF(state);

                offset += _roundSize;
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

        private byte[] GenerateHash()
        {
            byte[] output = new byte[HashSize];
            UpdateFinalTo(output);
            // Obtain the state data in the desired (hash) size we want.
            _hash = output;

            // Return the result.
            return output;
        }

        public ValueHash256 GenerateValueHash()
        {
            Unsafe.SkipInit(out ValueHash256 output);
            UpdateFinalTo(output.BytesAsSpan);
            return output;
        }

        public void UpdateFinalTo(Span<byte> output)
        {
            if (_hash is not null)
            {
                ThrowHashingComplete();
            }

            ulong[] state = _state;
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

                remainder = [];
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

                state = [];
            }
        }

        [SkipLocalsInit]
        public static void KeccakF1600Avx512F(Span<ulong> state)
        {
            ref ulong s = ref MemoryMarshal.GetReference(state);
            ref ulong rc = ref MemoryMarshal.GetArrayDataReference(RoundConstants);

            // Load 5x5 state as 5 rows (5 lanes used in each zmm)
            Vector512<ulong> c0 = Unsafe.As<ulong, Vector512<ulong>>(ref s);
            Vector512<ulong> c1 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 5));
            Vector512<ulong> c2 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 10));
            Vector512<ulong> c3 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 15));

            // Safe tail load for row4 (20..24) without over-read
            Vector256<ulong> c4lo = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref s, 20));
            Vector256<ulong> c4hi = Vector256.Create(Unsafe.Add(ref s, 24), 0UL, 0UL, 0UL);
            Vector512<ulong> c4 = Avx512F.InsertVector256(Vector512<ulong>.Zero, c4lo, 0);
            c4 = Avx512F.InsertVector256(c4, c4hi, 1);

            // Common lane permutes (rotate within the 5 live lanes)
            Vector512<ulong> rot4 = Vector512.Create(4UL, 0UL, 1UL, 2UL, 3UL, 5UL, 6UL, 7UL);
            Vector512<ulong> rot1 = Vector512.Create(1UL, 2UL, 3UL, 4UL, 0UL, 5UL, 6UL, 7UL);
            Vector512<ulong> rot2 = Vector512.Create(2UL, 3UL, 4UL, 0UL, 1UL, 5UL, 6UL, 7UL);
            Vector512<ulong> rot3 = Vector512.Create(3UL, 4UL, 0UL, 1UL, 2UL, 5UL, 6UL, 7UL);

            // Rho bit-rotation counts - PER ROW (y fixed, x varies)
            Vector512<ulong> rho0 = Vector512.Create(0UL, 1, 62, 28, 27, 0, 0, 0);
            Vector512<ulong> rho1 = Vector512.Create(36UL, 44, 6, 55, 20, 0, 0, 0);
            Vector512<ulong> rho2 = Vector512.Create(3UL, 10, 43, 25, 39, 0, 0, 0);
            Vector512<ulong> rho3 = Vector512.Create(41UL, 45, 15, 21, 8, 0, 0, 0);
            Vector512<ulong> rho4 = Vector512.Create(18UL, 2, 61, 56, 14, 0, 0, 0);

            Vector512<ulong> zero = Vector512<ulong>.Zero;

            for (int round = 0; round < ROUNDS; round += 2)
            {
                {
                    // Theta - 5-way xor via ternary logic
                    Vector512<ulong> parity = Avx512F.TernaryLogic(
                        Avx512F.TernaryLogic(c0, c1, c2, 0x96),
                        c3, c4, 0x96);

                    Vector512<ulong> theta = Avx512F.Xor(
                        Avx512F.PermuteVar8x64(parity, rot4),
                        Avx512F.RotateLeft(Avx512F.PermuteVar8x64(parity, rot1), 1));

                    c0 = Avx512F.Xor(c0, theta);
                    c1 = Avx512F.Xor(c1, theta);
                    c2 = Avx512F.Xor(c2, theta);
                    c3 = Avx512F.Xor(c3, theta);
                    c4 = Avx512F.Xor(c4, theta);

                    // Rho - per-lane bit rotates
                    c0 = Avx512F.RotateLeftVariable(c0, rho0);
                    c1 = Avx512F.RotateLeftVariable(c1, rho1);
                    c2 = Avx512F.RotateLeftVariable(c2, rho2);
                    c3 = Avx512F.RotateLeftVariable(c3, rho3);
                    c4 = Avx512F.RotateLeftVariable(c4, rho4);

                    // Pi - rotate each row by its row-index, then transpose (columns become Pi rows)
                    Vector512<ulong> r0 = c0;
                    Vector512<ulong> r1 = Avx512F.PermuteVar8x64(c1, rot1);
                    Vector512<ulong> r2 = Avx512F.PermuteVar8x64(c2, rot2);
                    Vector512<ulong> r3 = Avx512F.PermuteVar8x64(c3, rot3);
                    Vector512<ulong> r4 = Avx512F.PermuteVar8x64(c4, rot4);

                    // Treat missing rows as zero (rows 5..7)

                    // Stage 1 - unpack (interleave within 128-bit lanes)
                    Vector512<ulong> t0 = Avx512F.UnpackLow(r0, r1);
                    Vector512<ulong> t1 = Avx512F.UnpackHigh(r0, r1);
                    Vector512<ulong> t2 = Avx512F.UnpackLow(r2, r3);
                    Vector512<ulong> t3 = Avx512F.UnpackHigh(r2, r3);
                    Vector512<ulong> t4 = Avx512F.UnpackLow(r4, zero);
                    Vector512<ulong> t5 = Avx512F.UnpackHigh(r4, zero);

                    // Stage 2 - group (0,4), (2,6), (1,5), (3,7)
                    Vector512<ulong> s0 = Avx512F.Shuffle4x128(t0, t2, 0x44);
                    Vector512<ulong> s1 = Avx512F.Shuffle4x128(t0, t2, 0xEE);
                    Vector512<ulong> s2 = Avx512F.Shuffle4x128(t1, t3, 0x44);

                    Vector512<ulong> s4 = Avx512F.Shuffle4x128(t4, zero, 0x44);
                    Vector512<ulong> s5 = Avx512F.Shuffle4x128(t4, zero, 0xEE);
                    Vector512<ulong> s6 = Avx512F.Shuffle4x128(t5, zero, 0x44);

                    // Stage 3 - final columns (only need 0..4)
                    Vector512<ulong> col0 = Avx512F.Shuffle4x128(s0, s4, 0x88); // index 0
                    Vector512<ulong> col1 = Avx512F.Shuffle4x128(s2, s6, 0x88); // index 1
                    Vector512<ulong> col2 = Avx512F.Shuffle4x128(s0, s4, 0xDD); // index 2
                    Vector512<ulong> col3 = Avx512F.Shuffle4x128(s2, s6, 0xDD); // index 3
                    Vector512<ulong> col4 = Avx512F.Shuffle4x128(s1, s5, 0x88); // index 4

                    // Column-to-row remap: y=0,1,2,3,4 -> col0,col3,col1,col4,col2
                    Vector512<ulong> b0 = col0;
                    Vector512<ulong> b1 = col3;
                    Vector512<ulong> b2 = col1;
                    Vector512<ulong> b3 = col4;
                    Vector512<ulong> b4 = col2;

                    // Chi - row-wise ternary logic (same as your current)
                    c0 = Avx512F.TernaryLogic(b0, Avx512F.PermuteVar8x64(b0, rot1), Avx512F.PermuteVar8x64(b0, rot2), 0xD2);
                    c1 = Avx512F.TernaryLogic(b1, Avx512F.PermuteVar8x64(b1, rot1), Avx512F.PermuteVar8x64(b1, rot2), 0xD2);
                    c2 = Avx512F.TernaryLogic(b2, Avx512F.PermuteVar8x64(b2, rot1), Avx512F.PermuteVar8x64(b2, rot2), 0xD2);
                    c3 = Avx512F.TernaryLogic(b3, Avx512F.PermuteVar8x64(b3, rot1), Avx512F.PermuteVar8x64(b3, rot2), 0xD2);
                    c4 = Avx512F.TernaryLogic(b4, Avx512F.PermuteVar8x64(b4, rot1), Avx512F.PermuteVar8x64(b4, rot2), 0xD2);

                    // Iota - xor round constant into lane 0 only
                    c0 = Avx512F.Xor(c0, Vector512.CreateScalar(rc));
                    rc = ref Unsafe.Add(ref rc, 1);
                }
                {
                    // Theta - 5-way xor via ternary logic
                    Vector512<ulong> parity = Avx512F.TernaryLogic(
                        Avx512F.TernaryLogic(c0, c1, c2, 0x96),
                        c3, c4, 0x96);

                    Vector512<ulong> theta = Avx512F.Xor(
                        Avx512F.PermuteVar8x64(parity, rot4),
                        Avx512F.RotateLeft(Avx512F.PermuteVar8x64(parity, rot1), 1));

                    c0 = Avx512F.Xor(c0, theta);
                    c1 = Avx512F.Xor(c1, theta);
                    c2 = Avx512F.Xor(c2, theta);
                    c3 = Avx512F.Xor(c3, theta);
                    c4 = Avx512F.Xor(c4, theta);

                    // Rho - per-lane bit rotates
                    c0 = Avx512F.RotateLeftVariable(c0, rho0);
                    c1 = Avx512F.RotateLeftVariable(c1, rho1);
                    c2 = Avx512F.RotateLeftVariable(c2, rho2);
                    c3 = Avx512F.RotateLeftVariable(c3, rho3);
                    c4 = Avx512F.RotateLeftVariable(c4, rho4);

                    // Pi - rotate each row by its row-index, then transpose (columns become Pi rows)
                    Vector512<ulong> r0 = c0;
                    Vector512<ulong> r1 = Avx512F.PermuteVar8x64(c1, rot1);
                    Vector512<ulong> r2 = Avx512F.PermuteVar8x64(c2, rot2);
                    Vector512<ulong> r3 = Avx512F.PermuteVar8x64(c3, rot3);
                    Vector512<ulong> r4 = Avx512F.PermuteVar8x64(c4, rot4);

                    // Treat missing rows as zero (rows 5..7)

                    // Stage 1 - unpack (interleave within 128-bit lanes)
                    Vector512<ulong> t0 = Avx512F.UnpackLow(r0, r1);
                    Vector512<ulong> t1 = Avx512F.UnpackHigh(r0, r1);
                    Vector512<ulong> t2 = Avx512F.UnpackLow(r2, r3);
                    Vector512<ulong> t3 = Avx512F.UnpackHigh(r2, r3);
                    Vector512<ulong> t4 = Avx512F.UnpackLow(r4, zero);
                    Vector512<ulong> t5 = Avx512F.UnpackHigh(r4, zero);

                    // Stage 2 - group (0,4), (2,6), (1,5), (3,7)
                    Vector512<ulong> s0 = Avx512F.Shuffle4x128(t0, t2, 0x44);
                    Vector512<ulong> s1 = Avx512F.Shuffle4x128(t0, t2, 0xEE);
                    Vector512<ulong> s2 = Avx512F.Shuffle4x128(t1, t3, 0x44);

                    Vector512<ulong> s4 = Avx512F.Shuffle4x128(t4, zero, 0x44);
                    Vector512<ulong> s5 = Avx512F.Shuffle4x128(t4, zero, 0xEE);
                    Vector512<ulong> s6 = Avx512F.Shuffle4x128(t5, zero, 0x44);

                    // Stage 3 - final columns (only need 0..4)
                    Vector512<ulong> col0 = Avx512F.Shuffle4x128(s0, s4, 0x88); // index 0
                    Vector512<ulong> col1 = Avx512F.Shuffle4x128(s2, s6, 0x88); // index 1
                    Vector512<ulong> col2 = Avx512F.Shuffle4x128(s0, s4, 0xDD); // index 2
                    Vector512<ulong> col3 = Avx512F.Shuffle4x128(s2, s6, 0xDD); // index 3
                    Vector512<ulong> col4 = Avx512F.Shuffle4x128(s1, s5, 0x88); // index 4

                    // Column-to-row remap: y=0,1,2,3,4 -> col0,col3,col1,col4,col2
                    Vector512<ulong> b0 = col0;
                    Vector512<ulong> b1 = col3;
                    Vector512<ulong> b2 = col1;
                    Vector512<ulong> b3 = col4;
                    Vector512<ulong> b4 = col2;

                    // Chi - row-wise ternary logic (same as your current)
                    c0 = Avx512F.TernaryLogic(b0, Avx512F.PermuteVar8x64(b0, rot1), Avx512F.PermuteVar8x64(b0, rot2), 0xD2);
                    c1 = Avx512F.TernaryLogic(b1, Avx512F.PermuteVar8x64(b1, rot1), Avx512F.PermuteVar8x64(b1, rot2), 0xD2);
                    c2 = Avx512F.TernaryLogic(b2, Avx512F.PermuteVar8x64(b2, rot1), Avx512F.PermuteVar8x64(b2, rot2), 0xD2);
                    c3 = Avx512F.TernaryLogic(b3, Avx512F.PermuteVar8x64(b3, rot1), Avx512F.PermuteVar8x64(b3, rot2), 0xD2);
                    c4 = Avx512F.TernaryLogic(b4, Avx512F.PermuteVar8x64(b4, rot1), Avx512F.PermuteVar8x64(b4, rot2), 0xD2);

                    // Iota - xor round constant into lane 0 only
                    c0 = Avx512F.Xor(c0, Vector512.CreateScalar(rc));
                    rc = ref Unsafe.Add(ref rc, 1);
                }
            }

            // Store - same strategy as your original
            Unsafe.As<ulong, Vector512<ulong>>(ref s) = c0;
            Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 5)) = c1;
            Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 10)) = c2;
            Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 15)) = c3;

            Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref s, 20)) = c4.GetLower();
            Unsafe.Add(ref s, 24) = c4.GetElement(4);
        }
    }
}
