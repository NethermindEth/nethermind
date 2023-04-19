// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        public static void KeccakF(Span<ulong> st)
        {
            Debug.Assert(st.Length == 25);
            if (Avx2.IsSupported)
            {
                KeccakF1600_AVX2(st);
            }
            else
            {
                KeccakF1600_64bit(st);
            }
        }

        private static readonly Vector256<ulong>[] Iotas = new Vector256<ulong>[]
        {
            Vector256.Create(0x0000000000000001UL, 0x0000000000000001UL, 0x0000000000000001UL, 0x0000000000000001UL),
            Vector256.Create(0x0000000000008082UL, 0x0000000000008082UL, 0x0000000000008082UL, 0x0000000000008082UL),
            Vector256.Create(0x800000000000808aUL, 0x800000000000808aUL, 0x800000000000808aUL, 0x800000000000808aUL),
            Vector256.Create(0x8000000080008000UL, 0x8000000080008000UL, 0x8000000080008000UL, 0x8000000080008000UL),
            Vector256.Create(0x000000000000808bUL, 0x000000000000808bUL, 0x000000000000808bUL, 0x000000000000808bUL),
            Vector256.Create(0x0000000080000001UL, 0x0000000080000001UL, 0x0000000080000001UL, 0x0000000080000001UL),
            Vector256.Create(0x8000000080008081UL, 0x8000000080008081UL, 0x8000000080008081UL, 0x8000000080008081UL),
            Vector256.Create(0x8000000000008009UL, 0x8000000000008009UL, 0x8000000000008009UL, 0x8000000000008009UL),
            Vector256.Create(0x000000000000008aUL, 0x000000000000008aUL, 0x000000000000008aUL, 0x000000000000008aUL),
            Vector256.Create(0x0000000000000088UL, 0x0000000000000088UL, 0x0000000000000088UL, 0x0000000000000088UL),
            Vector256.Create(0x0000000080008009UL, 0x0000000080008009UL, 0x0000000080008009UL, 0x0000000080008009UL),
            Vector256.Create(0x000000008000000aUL, 0x000000008000000aUL, 0x000000008000000aUL, 0x000000008000000aUL),
            Vector256.Create(0x000000008000808bUL, 0x000000008000808bUL, 0x000000008000808bUL, 0x000000008000808bUL),
            Vector256.Create(0x800000000000008bUL, 0x800000000000008bUL, 0x800000000000008bUL, 0x800000000000008bUL),
            Vector256.Create(0x8000000000008089UL, 0x8000000000008089UL, 0x8000000000008089UL, 0x8000000000008089UL),
            Vector256.Create(0x8000000000008003UL, 0x8000000000008003UL, 0x8000000000008003UL, 0x8000000000008003UL),
            Vector256.Create(0x8000000000008002UL, 0x8000000000008002UL, 0x8000000000008002UL, 0x8000000000008002UL),
            Vector256.Create(0x8000000000000080UL, 0x8000000000000080UL, 0x8000000000000080UL, 0x8000000000000080UL),
            Vector256.Create(0x000000000000800aUL, 0x000000000000800aUL, 0x000000000000800aUL, 0x000000000000800aUL),
            Vector256.Create(0x800000008000000aUL, 0x800000008000000aUL, 0x800000008000000aUL, 0x800000008000000aUL),
            Vector256.Create(0x8000000080008081UL, 0x8000000080008081UL, 0x8000000080008081UL, 0x8000000080008081UL),
            Vector256.Create(0x8000000000008080UL, 0x8000000000008080UL, 0x8000000000008080UL, 0x8000000000008080UL),
            Vector256.Create(0x0000000080000001UL, 0x0000000080000001UL, 0x0000000080000001UL, 0x0000000080000001UL),
            Vector256.Create(0x8000000080008008UL, 0x8000000080008008UL, 0x8000000080008008UL, 0x8000000080008008UL),
        };

        private static readonly Vector256<ulong>[] RhotatesLeft = new Vector256<ulong>[]
        {
            Vector256.Create( 3ul,   18ul,    36ul,    41ul),         // [2][0] [4][0] [1][0] [3][0]
            Vector256.Create( 1ul,   62ul,    28ul,    27ul),         // [0][1] [0][2] [0][3] [0][4]
            Vector256.Create(45ul,    6ul,    56ul,    39ul),         // [3][1] [1][2] [4][3] [2][4]
            Vector256.Create(10ul,   61ul,    55ul,     8ul),         // [2][1] [4][2] [1][3] [3][4]
            Vector256.Create( 2ul,   15ul,    25ul,    20ul),         // [4][1] [3][2] [2][3] [1][4]
            Vector256.Create(44ul,   43ul,    21ul,    14ul),         // [1][1] [2][2] [3][3] [4][4]
        };

        private static readonly Vector256<ulong>[] RhotatesRight = new Vector256<ulong>[]
        {
            Vector256.Create(64-3ul,  64-18ul,  64-36ul,  64-41ul),
            Vector256.Create(64-1ul,  64-62ul,  64-28ul,  64-27ul),
            Vector256.Create(64-45ul, 64-6ul,   64-56ul,  64-39ul),
            Vector256.Create(64-10ul, 64-61ul,  64-55ul,  64-8ul),
            Vector256.Create(64-2ul,  64-15ul,  64-25ul,  64-20ul),
            Vector256.Create(64-44ul, 64-43ul,  64-21ul,  64-14ul),
        };

        [SkipLocalsInit]
        public static unsafe void KeccakF1600_AVX2(Span<ulong> state)
        {
            Vector256<ulong>[] iotas = Iotas;

            ref byte state0 = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(state));
            // Load state
            Vector256<ulong> ymm0 = Avx2.BroadcastScalarToVector256(Unsafe.ReadUnaligned<Vector128<ulong>>(ref state0));
            Vector256<ulong> ymm1 = Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 0));
            Vector256<ulong> ymm2 = Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 1));
            Vector256<ulong> ymm3 = Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 2));
            Vector256<ulong> ymm4 = Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 3));
            Vector256<ulong> ymm5 = Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 4));
            Vector256<ulong> ymm6 = Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 5));

            for (int i = 0; i < iotas.Length; i++)
            {
                var ymm13 = Avx2.Shuffle(ymm2.AsUInt32(), 0b01001110).AsUInt64();
                var ymm12 = Avx2.Xor(ymm5, ymm3);
                var ymm9 = Avx2.Xor(ymm4, ymm6);
                ymm12 = Avx2.Xor(ymm12, ymm1);
                ymm12 = Avx2.Xor(ymm12, ymm9);         // C[1..4]

                var ymm11 = Avx2.Permute4x64(ymm12, 0b10010011);
                ymm13 = Avx2.Xor(ymm13, ymm2);
                var ymm7 = Avx2.Permute4x64(ymm13, 0b01001110);

                var ymm8 = Avx2.ShiftRightLogical(ymm12, 63);
                ymm9 = Avx2.Add(ymm12, ymm12);
                ymm8 = Avx2.Or(ymm8, ymm9);           // ROL64(C[1..4],1)

                var ymm15 = Avx2.Permute4x64(ymm8, 0b00111001);
                var ymm14 = Avx2.Xor(ymm8, ymm11);
                ymm14 = Avx2.Permute4x64(ymm14, 0b00000000);   // D[0..0] = ROL64(C[1],1) ^ C[4]

                ymm13 = Avx2.Xor(ymm13, ymm0);
                ymm13 = Avx2.Xor(ymm13, ymm7);         // C[0..0]

                ymm7 = Avx2.ShiftRightLogical(ymm13, 63);
                ymm8 = Avx2.Add(ymm13, ymm13);
                ymm8 = Avx2.Or(ymm8, ymm7);            // ROL64(C[0..0],1)

                ymm2 = Avx2.Xor(ymm2, ymm14);          // ^= D[0..0]
                ymm0 = Avx2.Xor(ymm0, ymm14);          // ^= D[0..0]

                ymm15 = Avx2.Blend(ymm15.AsUInt32(), ymm8.AsUInt32(), 0b11000000).AsUInt64();
                ymm11 = Avx2.Blend(ymm11.AsUInt32(), ymm13.AsUInt32(), 0b00000011).AsUInt64();
                ymm15 = Avx2.Xor(ymm15, ymm11);        // D[1..4] = ROL64(C[2..4,0),1) ^ C[0..3]

                // Rho + Pi + pre-Chi shuffle
                var ymm10 = Avx2.ShiftLeftLogicalVariable(ymm2, RhotatesLeft[0]);
                ymm2 = Avx2.ShiftRightLogicalVariable(ymm2, RhotatesRight[0]);
                ymm2 = Avx2.Or(ymm2, ymm10);

                ymm3 = Avx2.Xor(ymm3, ymm15);                                  // ^= D[1..4] from Theta
                ymm11 = Avx2.ShiftLeftLogicalVariable(ymm3, RhotatesLeft[2]);
                ymm3 = Avx2.ShiftRightLogicalVariable(ymm3, RhotatesRight[2]);
                ymm3 = Avx2.Or(ymm3, ymm11);

                ymm4 = Avx2.Xor(ymm4, ymm15);                                  // ^= D[1..4] from Theta
                ymm12 = Avx2.ShiftLeftLogicalVariable(ymm4, RhotatesLeft[3]);
                ymm4 = Avx2.ShiftRightLogicalVariable(ymm4, RhotatesRight[3]);
                ymm4 = Avx2.Or(ymm4, ymm12);

                ymm5 = Avx2.Xor(ymm5, ymm15);                                  // ^= D[1..4] from Theta
                ymm13 = Avx2.ShiftLeftLogicalVariable(ymm5, RhotatesLeft[4]);
                ymm5 = Avx2.ShiftRightLogicalVariable(ymm5, RhotatesRight[4]);
                ymm5 = Avx2.Or(ymm5, ymm13);

                ymm6 = Avx2.Xor(ymm6, ymm15);                                  // ^= D[1..4] from Theta
                ymm10 = Avx2.Permute4x64(ymm2, 0b10001101);                    // %ymm2 -> future %ymm3
                ymm11 = Avx2.Permute4x64(ymm3, 0b10001101);                    // %ymm3 -> future %ymm4
                ymm14 = Avx2.ShiftLeftLogicalVariable(ymm6, RhotatesLeft[5]);
                ymm8 = Avx2.ShiftRightLogicalVariable(ymm6, RhotatesRight[5]);
                ymm8 = Avx2.Or(ymm8, ymm14);                                   // %ymm6 -> future %ymm1

                ymm1 = Avx2.Xor(ymm1, ymm15);                                  // ^= D[1..4] from Theta
                ymm12 = Avx2.Permute4x64(ymm4, 0b00011011);                    // %ymm4 -> future %ymm5
                ymm13 = Avx2.Permute4x64(ymm5, 0b01110010);                    // %ymm5 -> future %ymm6
                ymm15 = Avx2.ShiftLeftLogicalVariable(ymm1, RhotatesLeft[1]);
                ymm9 = Avx2.ShiftRightLogicalVariable(ymm1, RhotatesRight[1]);
                ymm9 = Avx2.Or(ymm9, ymm15);                                   // %ymm1 -> future %ymm2

                // Chi
                ymm14 = Avx2.ShiftRightLogical128BitLane(ymm8, 8);
                ymm7 = Avx2.AndNot(ymm8, ymm14);                                               // tgting  [0][0] [0][0] [0][0] [0][0]

                ymm3 = Avx2.Blend(ymm9.AsUInt32(), ymm13.AsUInt32(), 0b00001100).AsUInt64();   //               [4][4] [2][0]
                ymm15 = Avx2.Blend(ymm11.AsUInt32(), ymm9.AsUInt32(), 0b00001100).AsUInt64();  //               [4][0] [2][1]
                ymm5 = Avx2.Blend(ymm10.AsUInt32(), ymm11.AsUInt32(), 0b00001100).AsUInt64();  //               [4][2] [2][4]
                ymm14 = Avx2.Blend(ymm9.AsUInt32(), ymm10.AsUInt32(), 0b00001100).AsUInt64();  //               [4][3] [2][0]
                ymm3 = Avx2.Blend(ymm3.AsUInt32(), ymm11.AsUInt32(), 0b00110000).AsUInt64();   //        [1][3] [4][4] [2][0]
                ymm15 = Avx2.Blend(ymm15.AsUInt32(), ymm12.AsUInt32(), 0b00110000).AsUInt64(); //        [1][4] [4][0] [2][1]
                ymm5 = Avx2.Blend(ymm5.AsUInt32(), ymm9.AsUInt32(), 0b00110000).AsUInt64();    //        [1][0] [4][2] [2][4]
                ymm14 = Avx2.Blend(ymm14.AsUInt32(), ymm13.AsUInt32(), 0b00110000).AsUInt64(); //        [1][1] [4][3] [2][0]
                ymm3 = Avx2.Blend(ymm3.AsUInt32(), ymm12.AsUInt32(), 0b11000000).AsUInt64();   // [3][2] [1][3] [4][4] [2][0]
                ymm15 = Avx2.Blend(ymm15.AsUInt32(), ymm13.AsUInt32(), 0b11000000).AsUInt64(); // [3][3] [1][4] [4][0] [2][1]
                ymm5 = Avx2.Blend(ymm5.AsUInt32(), ymm13.AsUInt32(), 0b11000000).AsUInt64();   // [3][3] [1][0] [4][2] [2][4]
                ymm14 = Avx2.Blend(ymm14.AsUInt32(), ymm11.AsUInt32(), 0b11000000).AsUInt64(); // [3][4] [1][1] [4][3] [2][0]
                ymm3 = Avx2.AndNot(ymm3, ymm15);                                               // tgting  [3][1] [1][2] [4][3] [2][4]
                ymm5 = Avx2.AndNot(ymm5, ymm14);                                               // tgting  [3][2] [1][4] [4][1] [2][3]

                ymm6 = Avx2.Blend(ymm12.AsUInt32(), ymm9.AsUInt32(), 0b00001100).AsUInt64();   //               [4][0] [2][3]
                ymm15 = Avx2.Blend(ymm10.AsUInt32(), ymm12.AsUInt32(), 0b00001100).AsUInt64(); //               [4][1] [2][4]
                ymm3 = Avx2.Xor(ymm3, ymm10);
                ymm6 = Avx2.Blend(ymm6.AsUInt32(), ymm10.AsUInt32(), 0b00110000).AsUInt64();   //        [1][2] [4][0] [2][3]
                ymm15 = Avx2.Blend(ymm15.AsUInt32(), ymm11.AsUInt32(), 0b00110000).AsUInt64(); //        [1][3] [4][1] [2][4]
                ymm5 = Avx2.Xor(ymm5, ymm12);
                ymm6 = Avx2.Blend(ymm6.AsUInt32(), ymm11.AsUInt32(), 0b11000000).AsUInt64();   // [3][4] [1][2] [4][0] [2][3]
                ymm15 = Avx2.Blend(ymm15.AsUInt32(), ymm9.AsUInt32(), 0b11000000).AsUInt64();  // [3][0] [1][3] [4][1] [2][4]
                ymm6 = Avx2.AndNot(ymm6, ymm15);                                               // tgting  [3][3] [1][1] [4][4] [2][2]
                ymm6 = Avx2.Xor(ymm6, ymm13);

                ymm4 = Avx2.Permute4x64(ymm8, 0b00011110);                                     // [0][1] [0][2] [0][4] [0][3]
                ymm15 = Avx2.Blend(ymm4.AsUInt32(), ymm0.AsUInt32(), 0b00110000).AsUInt64();   // [0][1] [0][0] [0][4] [0][3]
                ymm1 = Avx2.Permute4x64(ymm8, 0b00111001);                                     // [0][1] [0][4] [0][3] [0][2]
                ymm1 = Avx2.Blend(ymm1.AsUInt32(), ymm0.AsUInt32(), 0b11000000).AsUInt64();    // [0][0] [0][4] [0][3] [0][2]
                ymm1 = Avx2.AndNot(ymm1, ymm15);                                               // tgting  [0][4] [0][3] [0][2] [0][1]

                ymm2 = Avx2.Blend(ymm11.AsUInt32(), ymm12.AsUInt32(), 0b00001100).AsUInt64();  //               [4][1] [2][1]
                ymm14 = Avx2.Blend(ymm13.AsUInt32(), ymm11.AsUInt32(), 0b00001100).AsUInt64(); //               [4][2] [2][2]
                ymm2 = Avx2.Blend(ymm2.AsUInt32(), ymm13.AsUInt32(), 0b00110000).AsUInt64();   //        [1][1] [4][1] [2][1]
                ymm14 = Avx2.Blend(ymm14.AsUInt32(), ymm10.AsUInt32(), 0b00110000).AsUInt64(); //        [1][2] [4][2] [2][2]
                ymm2 = Avx2.Blend(ymm2.AsUInt32(), ymm10.AsUInt32(), 0b11000000).AsUInt64();   // [3][1] [1][1] [4][1] [2][1]
                ymm14 = Avx2.Blend(ymm14.AsUInt32(), ymm12.AsUInt32(), 0b11000000).AsUInt64(); // [3][2] [1][2] [4][2] [2][2]
                ymm2 = Avx2.AndNot(ymm2, ymm14);                                               // tgting  [3][0] [1][0] [4][0] [2][0]
                ymm2 = Avx2.Xor(ymm2, ymm9);

                ymm7 = Avx2.Permute4x64(ymm7, 0b00000000);                                     // [0][0] [0][0] [0][0] [0][0]
                ymm3 = Avx2.Permute4x64(ymm3, 0b00011011);                                     // post-Chi shuffle
                ymm5 = Avx2.Permute4x64(ymm5, 0b10001101);
                ymm6 = Avx2.Permute4x64(ymm6, 0b01110010);

                ymm4 = Avx2.Blend(ymm13.AsUInt32(), ymm10.AsUInt32(), 0b00001100).AsUInt64();  //               [4][3] [2][2]
                ymm14 = Avx2.Blend(ymm12.AsUInt32(), ymm13.AsUInt32(), 0b00001100).AsUInt64(); //               [4][4] [2][3]
                ymm4 = Avx2.Blend(ymm4.AsUInt32(), ymm12.AsUInt32(), 0b00110000).AsUInt64();   //        [1][4] [4][3] [2][2]
                ymm14 = Avx2.Blend(ymm14.AsUInt32(), ymm9.AsUInt32(), 0b00110000).AsUInt64();  //        [1][0] [4][4] [2][3]
                ymm4 = Avx2.Blend(ymm4.AsUInt32(), ymm9.AsUInt32(), 0b11000000).AsUInt64();    // [3][0] [1][4] [4][3] [2][2]
                ymm14 = Avx2.Blend(ymm14.AsUInt32(), ymm10.AsUInt32(), 0b11000000).AsUInt64(); // [3][1] [1][0] [4][4] [2][3]
                ymm4 = Avx2.AndNot(ymm4, ymm14);                                               // tgting  [3][4] [1][3] [4][2] [2][1]

                ymm0 = Avx2.Xor(ymm0, ymm7);
                ymm1 = Avx2.Xor(ymm1, ymm8);
                ymm4 = Avx2.Xor(ymm4, ymm11);

                // Iota
                ymm0 = Avx2.Xor(ymm0, iotas[i]);
            }

            var temp = ymm0;
            Unsafe.WriteUnaligned<Vector128<ulong>>(ref state0, Unsafe.As<Vector256<ulong>, Vector128<ulong>>(ref temp));
            Unsafe.WriteUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 0), ymm1);
            Unsafe.WriteUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 1), ymm2);
            Unsafe.WriteUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 2), ymm3);
            Unsafe.WriteUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 3), ymm4);
            Unsafe.WriteUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 4), ymm5);
            Unsafe.WriteUnaligned<Vector256<ulong>>(ref Unsafe.AddByteOffset(ref state0, sizeof(Vector128<ulong>) + sizeof(Vector256<ulong>) * 5), ymm6);
        }

        public static void KeccakF1600_64bit(Span<ulong> st)
        {
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
                da = bCu ^ BitOperations.RotateLeft(bCe, 1);
                de = bCa ^ BitOperations.RotateLeft(bCi, 1);
                di = bCe ^ BitOperations.RotateLeft(bCo, 1);
                @do = bCi ^ BitOperations.RotateLeft(bCu, 1);
                du = bCo ^ BitOperations.RotateLeft(bCa, 1);

                aba ^= da;
                bCa = aba;
                age ^= de;
                bCe = BitOperations.RotateLeft(age, 44);
                aki ^= di;
                bCi = BitOperations.RotateLeft(aki, 43);
                amo ^= @do;
                bCo = BitOperations.RotateLeft(amo, 21);
                asu ^= du;
                bCu = BitOperations.RotateLeft(asu, 14);
                eba = bCa ^ ((~bCe) & bCi);
                eba ^= RoundConstants[round];
                ebe = bCe ^ ((~bCi) & bCo);
                ebi = bCi ^ ((~bCo) & bCu);
                ebo = bCo ^ ((~bCu) & bCa);
                ebu = bCu ^ ((~bCa) & bCe);

                abo ^= @do;
                bCa = BitOperations.RotateLeft(abo, 28);
                agu ^= du;
                bCe = BitOperations.RotateLeft(agu, 20);
                aka ^= da;
                bCi = BitOperations.RotateLeft(aka, 3);
                ame ^= de;
                bCo = BitOperations.RotateLeft(ame, 45);
                asi ^= di;
                bCu = BitOperations.RotateLeft(asi, 61);
                ega = bCa ^ ((~bCe) & bCi);
                ege = bCe ^ ((~bCi) & bCo);
                egi = bCi ^ ((~bCo) & bCu);
                ego = bCo ^ ((~bCu) & bCa);
                egu = bCu ^ ((~bCa) & bCe);

                abe ^= de;
                bCa = BitOperations.RotateLeft(abe, 1);
                agi ^= di;
                bCe = BitOperations.RotateLeft(agi, 6);
                ako ^= @do;
                bCi = BitOperations.RotateLeft(ako, 25);
                amu ^= du;
                bCo = BitOperations.RotateLeft(amu, 8);
                asa ^= da;
                bCu = BitOperations.RotateLeft(asa, 18);
                eka = bCa ^ ((~bCe) & bCi);
                eke = bCe ^ ((~bCi) & bCo);
                eki = bCi ^ ((~bCo) & bCu);
                eko = bCo ^ ((~bCu) & bCa);
                eku = bCu ^ ((~bCa) & bCe);

                abu ^= du;
                bCa = BitOperations.RotateLeft(abu, 27);
                aga ^= da;
                bCe = BitOperations.RotateLeft(aga, 36);
                ake ^= de;
                bCi = BitOperations.RotateLeft(ake, 10);
                ami ^= di;
                bCo = BitOperations.RotateLeft(ami, 15);
                aso ^= @do;
                bCu = BitOperations.RotateLeft(aso, 56);
                ema = bCa ^ ((~bCe) & bCi);
                eme = bCe ^ ((~bCi) & bCo);
                emi = bCi ^ ((~bCo) & bCu);
                emo = bCo ^ ((~bCu) & bCa);
                emu = bCu ^ ((~bCa) & bCe);

                abi ^= di;
                bCa = BitOperations.RotateLeft(abi, 62);
                ago ^= @do;
                bCe = BitOperations.RotateLeft(ago, 55);
                aku ^= du;
                bCi = BitOperations.RotateLeft(aku, 39);
                ama ^= da;
                bCo = BitOperations.RotateLeft(ama, 41);
                ase ^= de;
                bCu = BitOperations.RotateLeft(ase, 2);
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
                da = bCu ^ BitOperations.RotateLeft(bCe, 1);
                de = bCa ^ BitOperations.RotateLeft(bCi, 1);
                di = bCe ^ BitOperations.RotateLeft(bCo, 1);
                @do = bCi ^ BitOperations.RotateLeft(bCu, 1);
                du = bCo ^ BitOperations.RotateLeft(bCa, 1);

                eba ^= da;
                bCa = eba;
                ege ^= de;
                bCe = BitOperations.RotateLeft(ege, 44);
                eki ^= di;
                bCi = BitOperations.RotateLeft(eki, 43);
                emo ^= @do;
                bCo = BitOperations.RotateLeft(emo, 21);
                esu ^= du;
                bCu = BitOperations.RotateLeft(esu, 14);
                aba = bCa ^ ((~bCe) & bCi);
                aba ^= RoundConstants[round + 1];
                abe = bCe ^ ((~bCi) & bCo);
                abi = bCi ^ ((~bCo) & bCu);
                abo = bCo ^ ((~bCu) & bCa);
                abu = bCu ^ ((~bCa) & bCe);

                ebo ^= @do;
                bCa = BitOperations.RotateLeft(ebo, 28);
                egu ^= du;
                bCe = BitOperations.RotateLeft(egu, 20);
                eka ^= da;
                bCi = BitOperations.RotateLeft(eka, 3);
                eme ^= de;
                bCo = BitOperations.RotateLeft(eme, 45);
                esi ^= di;
                bCu = BitOperations.RotateLeft(esi, 61);
                aga = bCa ^ ((~bCe) & bCi);
                age = bCe ^ ((~bCi) & bCo);
                agi = bCi ^ ((~bCo) & bCu);
                ago = bCo ^ ((~bCu) & bCa);
                agu = bCu ^ ((~bCa) & bCe);

                ebe ^= de;
                bCa = BitOperations.RotateLeft(ebe, 1);
                egi ^= di;
                bCe = BitOperations.RotateLeft(egi, 6);
                eko ^= @do;
                bCi = BitOperations.RotateLeft(eko, 25);
                emu ^= du;
                bCo = BitOperations.RotateLeft(emu, 8);
                esa ^= da;
                bCu = BitOperations.RotateLeft(esa, 18);
                aka = bCa ^ ((~bCe) & bCi);
                ake = bCe ^ ((~bCi) & bCo);
                aki = bCi ^ ((~bCo) & bCu);
                ako = bCo ^ ((~bCu) & bCa);
                aku = bCu ^ ((~bCa) & bCe);

                ebu ^= du;
                bCa = BitOperations.RotateLeft(ebu, 27);
                ega ^= da;
                bCe = BitOperations.RotateLeft(ega, 36);
                eke ^= de;
                bCi = BitOperations.RotateLeft(eke, 10);
                emi ^= di;
                bCo = BitOperations.RotateLeft(emi, 15);
                eso ^= @do;
                bCu = BitOperations.RotateLeft(eso, 56);
                ama = bCa ^ ((~bCe) & bCi);
                ame = bCe ^ ((~bCi) & bCo);
                ami = bCi ^ ((~bCo) & bCu);
                amo = bCo ^ ((~bCu) & bCa);
                amu = bCu ^ ((~bCa) & bCe);

                ebi ^= di;
                bCa = BitOperations.RotateLeft(ebi, 62);
                ego ^= @do;
                bCe = BitOperations.RotateLeft(ego, 55);
                eku ^= du;
                bCi = BitOperations.RotateLeft(eku, 39);
                ema ^= da;
                bCo = BitOperations.RotateLeft(ema, 41);
                ese ^= de;
                bCu = BitOperations.RotateLeft(ese, 2);
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

        public void Update(Span<byte> input)
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
                Span<byte> remainderAdditive = input[..Math.Min(input.Length, _roundSize - _remainderLength)];
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
                Span<ulong> input64 = MemoryMarshal.Cast<byte, ulong>(input[.._roundSize]);

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
