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

[assembly: InternalsVisibleTo("Nethermind.Benchmark")]

// ReSharper disable InconsistentNaming
namespace Nethermind.Core.Crypto;

public sealed class KeccakHash
{
    public const int HASH_SIZE = 32;
    private const int STATE_SIZE = 200;
    private const int HASH_DATA_AREA = 136;
    private const int ROUNDS = 24;
    private const int LANE_BITS = 8 * 8;
    private const int TEMP_BUFF_SIZE = 144;
    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL,
        0x8000000080008000UL, 0x000000000000808bUL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008aUL,
        0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL,
        0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
        0x000000000000800aUL, 0x800000008000000aUL, 0x8000000080008081UL,
        0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    ];

    // Rho rotate counts in OpenSSL AVX2 lane order (4 lanes per ymm).
    // Left counts, then Right counts = 64 - Left.
    private static readonly ulong[] RhoLeft = [
        3UL, 18, 36, 41,   // table 0
        1UL, 62, 28, 27,   // table 1
        45UL, 6, 56, 39,   // table 2
        10UL, 61, 55, 8,   // table 3
        2UL, 15, 25, 20,   // table 4
        44UL, 43, 21, 14,  // table 5
    ];

    private static readonly ulong[] RhoRight = [
        61UL, 46, 28, 23,  // 64 - table 0
        63UL, 2, 36, 37,   // 64 - table 1
        19UL, 58, 8, 25,   // 64 - table 2
        54UL, 3, 9, 56,    // 64 - table 3
        62UL, 49, 39, 44,  // 64 - table 4
        20UL, 21, 43, 50,  // 64 - table 5
    ];

    // 24 vectors - each round constant repeated 4 times (so Iota is a single vpxor ymm, ymm, m256)
    private static readonly ulong[] Iota256 = [
        0x0000000000000001UL, 0x0000000000000001UL, 0x0000000000000001UL, 0x0000000000000001UL,
        0x0000000000008082UL, 0x0000000000008082UL, 0x0000000000008082UL, 0x0000000000008082UL,
        0x800000000000808AUL, 0x800000000000808AUL, 0x800000000000808AUL, 0x800000000000808AUL,
        0x8000000080008000UL, 0x8000000080008000UL, 0x8000000080008000UL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x000000000000808BUL, 0x000000000000808BUL, 0x000000000000808BUL,
        0x0000000080000001UL, 0x0000000080000001UL, 0x0000000080000001UL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000080008081UL, 0x8000000080008081UL, 0x8000000080008081UL,
        0x8000000000008009UL, 0x8000000000008009UL, 0x8000000000008009UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x000000000000008AUL, 0x000000000000008AUL, 0x000000000000008AUL,
        0x0000000000000088UL, 0x0000000000000088UL, 0x0000000000000088UL, 0x0000000000000088UL,
        0x0000000080008009UL, 0x0000000080008009UL, 0x0000000080008009UL, 0x0000000080008009UL,
        0x000000008000000AUL, 0x000000008000000AUL, 0x000000008000000AUL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x000000008000808BUL, 0x000000008000808BUL, 0x000000008000808BUL,
        0x800000000000008BUL, 0x800000000000008BUL, 0x800000000000008BUL, 0x800000000000008BUL,
        0x8000000000008089UL, 0x8000000000008089UL, 0x8000000000008089UL, 0x8000000000008089UL,
        0x8000000000008003UL, 0x8000000000008003UL, 0x8000000000008003UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000008002UL, 0x8000000000008002UL, 0x8000000000008002UL,
        0x8000000000000080UL, 0x8000000000000080UL, 0x8000000000000080UL, 0x8000000000000080UL,
        0x000000000000800AUL, 0x000000000000800AUL, 0x000000000000800AUL, 0x000000000000800AUL,
        0x800000008000000AUL, 0x800000008000000AUL, 0x800000008000000AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000080008081UL, 0x8000000080008081UL, 0x8000000080008081UL,
        0x8000000000008080UL, 0x8000000000008080UL, 0x8000000000008080UL, 0x8000000000008080UL,
        0x0000000080000001UL, 0x0000000080000001UL, 0x0000000080000001UL, 0x0000000080000001UL,
        0x8000000080008008UL, 0x8000000080008008UL, 0x8000000080008008UL, 0x8000000080008008UL,
    ];

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
        //else if (Avx2.IsSupported)
        //{
        //    // Not good yet
        //    KeccakF1600Avx2(st);
        //}
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

    internal enum Implementation
    {
        Software,
        Avx2,
        Avx512
    }

    internal static void BenchmarkHash(ReadOnlySpan<byte> input, Span<byte> output, Implementation implementation)
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
                switch (implementation)
                {
                    case Implementation.Avx512:
                        KeccakF1600Avx512F(state);
                        break;
                    case Implementation.Avx2:
                        KeccakF1600Avx2(state);
                        break;
                    default:
                        KeccakF1600(state);
                        break;
                }
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
        switch (implementation)
        {
            case Implementation.Avx512:
                KeccakF1600Avx512F(state);
                break;
            case Implementation.Avx2:
                KeccakF1600Avx2(state);
                break;
            default:
                KeccakF1600(state);
                break;
        }

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
    private static void XorVectors(Span<byte> state, ReadOnlySpan<byte> input)
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
    private static void KeccakF1600Avx512F(Span<ulong> state)
    {
        ref ulong s = ref MemoryMarshal.GetReference(state);
        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);

        // State layout:
        // - Each zmm holds one Keccak row (y fixed, x varies) in lanes 0-4.
        // - Lanes 5-7 are treated as "dead" and must never be permuted into lanes 0-4.
        Vector512<ulong> c0 = Unsafe.As<ulong, Vector512<ulong>>(ref s);
        Vector512<ulong> c1 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 5));
        Vector512<ulong> c2 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 10));
        Vector512<ulong> c3 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 15));

        // Safe tail load for row4 (20..24) without over-read.
        // Note: lanes 5-7 remain dead - we keep them explicitly zero here for determinism.
        Vector256<ulong> c4lo = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref s, 20));
        Vector256<ulong> c4hi = Vector256.Create(Unsafe.Add(ref s, 24), 0UL, 0UL, 0UL);
        Vector512<ulong> c4 = Avx512F.InsertVector256(Vector512<ulong>.Zero, c4lo, 0);
        c4 = Avx512F.InsertVector256(c4, c4hi, 1);

        // Theta lane rotates - rotate only lanes 0-4, keep lanes 5-7 fixed.
        Vector512<ulong> rot4 = Vector512.Create(4UL, 0UL, 1UL, 2UL, 3UL, 5UL, 6UL, 7UL);
        Vector512<ulong> rot1 = Vector512.Create(1UL, 2UL, 3UL, 4UL, 0UL, 5UL, 6UL, 7UL);

        // Pi-to-columns indices:
        // After these permutes, register i holds Pi output COLUMN i (x fixed, y varies) in lanes 0-4.
        // Lanes 5-7 remain dead.
        Vector512<ulong> pi0 = Vector512.Create(0UL, 3UL, 1UL, 4UL, 2UL, 5UL, 6UL, 7UL);
        Vector512<ulong> pi1 = Vector512.Create(1UL, 4UL, 2UL, 0UL, 3UL, 5UL, 6UL, 7UL);
        Vector512<ulong> pi2 = Vector512.Create(2UL, 0UL, 3UL, 1UL, 4UL, 5UL, 6UL, 7UL);
        Vector512<ulong> pi3 = Vector512.Create(3UL, 1UL, 4UL, 2UL, 0UL, 5UL, 6UL, 7UL);
        Vector512<ulong> pi4 = Vector512.Create(4UL, 2UL, 0UL, 3UL, 1UL, 5UL, 6UL, 7UL);

        // Rho rotate counts per row (y fixed, x varies) - used by vprolvq.
        Vector512<ulong> rho0 = Vector512.Create(0UL, 1, 62, 28, 27, 0, 0, 0);
        Vector512<ulong> rho1 = Vector512.Create(36UL, 44, 6, 55, 20, 0, 0, 0);
        Vector512<ulong> rho2 = Vector512.Create(3UL, 10, 43, 25, 39, 0, 0, 0);
        Vector512<ulong> rho3 = Vector512.Create(41UL, 45, 15, 21, 8, 0, 0, 0);
        Vector512<ulong> rho4 = Vector512.Create(18UL, 2, 61, 56, 14, 0, 0, 0);

        // 2 rounds per iteration - count down to keep loop control tight.
        for (int i = ROUNDS / 2; i != 0; i--)
        {
            // Round 0
            {
                // Theta
                Vector512<ulong> parity = Avx512F.TernaryLogic(
                    Avx512F.TernaryLogic(c0, c1, c2, 0x96),
                    c3, c4, 0x96);

                Vector512<ulong> theta1a = Avx512F.PermuteVar8x64(parity, rot1);
                Vector512<ulong> theta0 = Avx512F.PermuteVar8x64(parity, rot4);
                Vector512<ulong> theta1 = Avx512F.RotateLeft(theta1a, 1);

                c0 = Avx512F.TernaryLogic(c0, theta0, theta1, 0x96);
                c1 = Avx512F.TernaryLogic(c1, theta0, theta1, 0x96);
                c2 = Avx512F.TernaryLogic(c2, theta0, theta1, 0x96);
                c3 = Avx512F.TernaryLogic(c3, theta0, theta1, 0x96);
                c4 = Avx512F.TernaryLogic(c4, theta0, theta1, 0x96);

                // Rho + Pi-to-columns pipelining:
                // - Rho keeps row layout (y fixed, x varies).
                // - Pi permutes each row i into column i (x fixed, y varies) in the same register.
                c0 = Avx512F.RotateLeftVariable(c0, rho0);
                c1 = Avx512F.RotateLeftVariable(c1, rho1);
                c0 = Avx512F.PermuteVar8x64(c0, pi0);
                c2 = Avx512F.RotateLeftVariable(c2, rho2);
                c1 = Avx512F.PermuteVar8x64(c1, pi1);
                c3 = Avx512F.RotateLeftVariable(c3, rho3);
                c2 = Avx512F.PermuteVar8x64(c2, pi2);
                c4 = Avx512F.RotateLeftVariable(c4, rho4);
                c3 = Avx512F.PermuteVar8x64(c3, pi3);
                c4 = Avx512F.PermuteVar8x64(c4, pi4);

                // Chi (cross-register): each register is a column, lanes are y.
                // chi(a,b,c) = a ^ (~b & c) using imm8=0xD2
                Vector512<ulong> t0 = c0;
                Vector512<ulong> t1 = c1;
                c0 = Avx512F.TernaryLogic(c0, c1, c2, 0xD2);
                c1 = Avx512F.TernaryLogic(c1, c2, c3, 0xD2);
                c2 = Avx512F.TernaryLogic(c2, c3, c4, 0xD2);
                c3 = Avx512F.TernaryLogic(c3, c4, t0, 0xD2);
                c4 = Avx512F.TernaryLogic(c4, t0, t1, 0xD2);

                // Iota: xor RC into s0. In this column-layout, s0 is still lane0 of c0.
                c0 = Avx512F.Xor(c0, Vector512.CreateScalar(roundConstants));
                roundConstants = ref Unsafe.Add(ref roundConstants, 1);

                // Transpose columns -> rows (reuse your existing transpose core).
                Vector512<ulong> u0 = Avx512F.UnpackLow(c0, c1);
                Vector512<ulong> u2 = Avx512F.UnpackLow(c2, c3);
                Vector512<ulong> u1 = Avx512F.UnpackHigh(c0, c1);
                Vector512<ulong> u3 = Avx512F.UnpackHigh(c2, c3);

                Vector512<ulong> e4 = Avx512F.UnpackLow(c4, c4);
                Vector512<ulong> o4 = Avx512F.UnpackHigh(c4, c4);

                Vector512<ulong> s0 = Avx512F.Shuffle4x128(u0, u2, 0x44);
                Vector512<ulong> s2 = Avx512F.Shuffle4x128(u1, u3, 0x44);
                Vector512<ulong> s1 = Avx512F.Shuffle4x128(u0, u2, 0xEE);

                // Output order (0,3,1,4,2) - remap back to (0,1,2,3,4)
                Vector512<ulong> row0 = Avx512F.Shuffle4x128(s0, e4, 0x88);
                Vector512<ulong> row3 = Avx512F.Shuffle4x128(s2, o4, 0xDD);
                Vector512<ulong> row1 = Avx512F.Shuffle4x128(s2, o4, 0x88);
                Vector512<ulong> row4 = Avx512F.Shuffle4x128(s1, e4, 0xA8);
                Vector512<ulong> row2 = Avx512F.Shuffle4x128(s0, e4, 0xDD);

                c0 = row0;
                c1 = row1;
                c2 = row2;
                c3 = row3;
                c4 = row4;
            }
            // Round 1 (unrolled)
            {
                Vector512<ulong> parity = Avx512F.TernaryLogic(
                    Avx512F.TernaryLogic(c0, c1, c2, 0x96),
                    c3, c4, 0x96);

                Vector512<ulong> theta1a = Avx512F.PermuteVar8x64(parity, rot1);
                Vector512<ulong> theta0 = Avx512F.PermuteVar8x64(parity, rot4);
                Vector512<ulong> theta1 = Avx512F.RotateLeft(theta1a, 1);

                c0 = Avx512F.TernaryLogic(c0, theta0, theta1, 0x96);
                c1 = Avx512F.TernaryLogic(c1, theta0, theta1, 0x96);
                c2 = Avx512F.TernaryLogic(c2, theta0, theta1, 0x96);
                c3 = Avx512F.TernaryLogic(c3, theta0, theta1, 0x96);
                c4 = Avx512F.TernaryLogic(c4, theta0, theta1, 0x96);

                c0 = Avx512F.RotateLeftVariable(c0, rho0);
                c1 = Avx512F.RotateLeftVariable(c1, rho1);
                c0 = Avx512F.PermuteVar8x64(c0, pi0);
                c2 = Avx512F.RotateLeftVariable(c2, rho2);
                c1 = Avx512F.PermuteVar8x64(c1, pi1);
                c3 = Avx512F.RotateLeftVariable(c3, rho3);
                c2 = Avx512F.PermuteVar8x64(c2, pi2);
                c4 = Avx512F.RotateLeftVariable(c4, rho4);
                c3 = Avx512F.PermuteVar8x64(c3, pi3);
                c4 = Avx512F.PermuteVar8x64(c4, pi4);

                Vector512<ulong> t0 = c0;
                Vector512<ulong> t1 = c1;
                c0 = Avx512F.TernaryLogic(c0, c1, c2, 0xD2);
                c1 = Avx512F.TernaryLogic(c1, c2, c3, 0xD2);
                c2 = Avx512F.TernaryLogic(c2, c3, c4, 0xD2);
                c3 = Avx512F.TernaryLogic(c3, c4, t0, 0xD2);
                c4 = Avx512F.TernaryLogic(c4, t0, t1, 0xD2);

                c0 = Avx512F.Xor(c0, Vector512.CreateScalar(roundConstants));
                roundConstants = ref Unsafe.Add(ref roundConstants, 1);

                Vector512<ulong> u0 = Avx512F.UnpackLow(c0, c1);
                Vector512<ulong> u2 = Avx512F.UnpackLow(c2, c3);
                Vector512<ulong> u1 = Avx512F.UnpackHigh(c0, c1);
                Vector512<ulong> u3 = Avx512F.UnpackHigh(c2, c3);

                Vector512<ulong> e4 = Avx512F.UnpackLow(c4, c4);
                Vector512<ulong> o4 = Avx512F.UnpackHigh(c4, c4);

                Vector512<ulong> s0 = Avx512F.Shuffle4x128(u0, u2, 0x44);
                Vector512<ulong> s2 = Avx512F.Shuffle4x128(u1, u3, 0x44);
                Vector512<ulong> s1 = Avx512F.Shuffle4x128(u0, u2, 0xEE);

                Vector512<ulong> row0 = Avx512F.Shuffle4x128(s0, e4, 0x88);
                Vector512<ulong> row3 = Avx512F.Shuffle4x128(s2, o4, 0xDD);
                Vector512<ulong> row1 = Avx512F.Shuffle4x128(s2, o4, 0x88);
                Vector512<ulong> row4 = Avx512F.Shuffle4x128(s1, e4, 0xA8);
                Vector512<ulong> row2 = Avx512F.Shuffle4x128(s0, e4, 0xDD);

                c0 = row0;
                c1 = row1;
                c2 = row2;
                c3 = row3;
                c4 = row4;
            }
        }

        // Store rows 0-3 as full zmm; row4 as 4 lanes + scalar lane4.
        Unsafe.As<ulong, Vector512<ulong>>(ref s) = c0;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 5)) = c1;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 10)) = c2;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref s, 15)) = c3;

        Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref s, 20)) = c4.GetLower();
        Unsafe.Add(ref s, 24) = c4.GetElement(4);
    }

    [SkipLocalsInit]
    public static void KeccakF1600Avx2(Span<ulong> state)
    {
        ref ulong s = ref MemoryMarshal.GetReference(state);

        Vector256<ulong> a00 = Vector256.Create(s);
        Vector256<ulong> a01 = Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.As<ulong, byte>(ref Unsafe.Add(ref s, 1)));

        Vector256<ulong> a20 = Vector256.Create(
            Unsafe.Add(ref s, 10),
            Unsafe.Add(ref s, 20),
            Unsafe.Add(ref s, 5),
            Unsafe.Add(ref s, 15));

        Vector256<ulong> a31 = Vector256.Create(
            Unsafe.Add(ref s, 16),
            Unsafe.Add(ref s, 7),
            Unsafe.Add(ref s, 23),
            Unsafe.Add(ref s, 14));

        Vector256<ulong> a21 = Vector256.Create(
            Unsafe.Add(ref s, 11),
            Unsafe.Add(ref s, 22),
            Unsafe.Add(ref s, 8),
            Unsafe.Add(ref s, 19));

        Vector256<ulong> a41 = Vector256.Create(
            Unsafe.Add(ref s, 21),
            Unsafe.Add(ref s, 17),
            Unsafe.Add(ref s, 13),
            Unsafe.Add(ref s, 9));

        Vector256<ulong> a11 = Vector256.Create(
            Unsafe.Add(ref s, 6),
            Unsafe.Add(ref s, 12),
            Unsafe.Add(ref s, 18),
            Unsafe.Add(ref s, 24));

        // Constant bases as byte refs so offsets are in bytes (32 bytes per ymm table entry)
        ref byte rhoL = ref Unsafe.As<ulong, byte>(ref MemoryMarshal.GetArrayDataReference(RhoLeft));
        ref byte rhoR = ref Unsafe.As<ulong, byte>(ref MemoryMarshal.GetArrayDataReference(RhoRight));
        ref byte iota = ref Unsafe.As<ulong, byte>(ref MemoryMarshal.GetArrayDataReference(Iota256));

        for (int round = ROUNDS; round != 0; round--)
        {
            // Theta (as before)
            Vector256<ulong> c00 = Avx2.Shuffle(a20.AsUInt32(), 0x4E).AsUInt64();
            Vector256<ulong> c14 = Avx2.Xor(a31, a41);
            Vector256<ulong> t2  = Avx2.Xor(a11, a21);
            c14 = Avx2.Xor(a01, c14);
            c14 = Avx2.Xor(t2, c14);                              // C[1..4]

            Vector256<ulong> t4  = Avx2.Permute4x64(c14, 0x93);

            c00 = Avx2.Xor(a20, c00);
            Vector256<ulong> t0  = Avx2.Permute4x64(c00, 0x4E);

            Vector256<ulong> t1  = Avx2.ShiftRightLogical(c14, 63);
            t2 = Avx2.Add(c14, c14);
            t1 = Avx2.Or(t2, t1);                                 // ROL(C[1..4],1)

            Vector256<ulong> d14 = Avx2.Permute4x64(t1, 0x39);

            Vector256<ulong> d00 = Avx2.Xor(t4, t1);
            d00 = Avx2.Permute4x64(d00, 0x00);                    // broadcast D0

            c00 = Avx2.Xor(a00, c00);
            c00 = Avx2.Xor(t0, c00);                              // C0 broadcast

            t0 = Avx2.ShiftRightLogical(c00, 63);
            t1 = Avx2.Add(c00, c00);
            t1 = Avx2.Or(t0, t1);                                 // ROL(C0,1) broadcast

            a20 = Avx2.Xor(d00, a20);
            a00 = Avx2.Xor(d00, a00);

            d14 = Avx2.Blend(d14.AsUInt32(), t1.AsUInt32(), 0xC0).AsUInt64();
            t4  = Avx2.Blend(t4.AsUInt32(),  c00.AsUInt32(), 0x03).AsUInt64();
            d14 = Avx2.Xor(t4, d14);                              // D[1..4]

            // Rho + Pi, OpenSSL scheduling
            // We reuse t0,t1 as shift temps to keep reg pressure down.

            // A20 rotate (rho table0 @ +0x00) and Pi permute to p31
            t0 = Avx2.ShiftLeftLogicalVariable(a20, Unsafe.ReadUnaligned<Vector256<ulong>>(ref rhoL));
            t1 = Avx2.ShiftRightLogicalVariable(a20, Unsafe.ReadUnaligned<Vector256<ulong>>(ref rhoR));
            a20 = Avx2.Or(t0, t1);
            Vector256<ulong> p31 = Avx2.Permute4x64(a20, 0x8D);

            // A31 ^= D14, rotate (table2 @ +0x40), Pi permute to p21
            a31 = Avx2.Xor(d14, a31);
            t0  = Avx2.ShiftLeftLogicalVariable(a31, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoL, 0x40)));
            t1  = Avx2.ShiftRightLogicalVariable(a31, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoR, 0x40)));
            a31 = Avx2.Or(t0, t1);
            Vector256<ulong> p21 = Avx2.Permute4x64(a31, 0x8D);

            // A21 ^= D14, rotate (table3 @ +0x60), Pi permute to p41
            a21 = Avx2.Xor(d14, a21);
            t0  = Avx2.ShiftLeftLogicalVariable(a21, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoL, 0x60)));
            t1  = Avx2.ShiftRightLogicalVariable(a21, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoR, 0x60)));
            a21 = Avx2.Or(t0, t1);
            Vector256<ulong> p41 = Avx2.Permute4x64(a21, 0x1B);

            // A41 ^= D14, rotate (table4 @ +0x80), Pi permute to p11
            a41 = Avx2.Xor(d14, a41);
            t0  = Avx2.ShiftLeftLogicalVariable(a41, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoL, 0x80)));
            t1  = Avx2.ShiftRightLogicalVariable(a41, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoR, 0x80)));
            a41 = Avx2.Or(t0, t1);
            Vector256<ulong> p11 = Avx2.Permute4x64(a41, 0x72);

            // A11 ^= D14, rotate (table5 @ +0xA0) - becomes p01
            a11 = Avx2.Xor(d14, a11);
            t0  = Avx2.ShiftLeftLogicalVariable(a11, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoL, 0xA0)));
            t1  = Avx2.ShiftRightLogicalVariable(a11, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoR, 0xA0)));
            Vector256<ulong> p01 = Avx2.Or(t0, t1);

            // A01 ^= D14, rotate (table1 @ +0x20) - becomes p20
            a01 = Avx2.Xor(d14, a01);
            t0  = Avx2.ShiftLeftLogicalVariable(a01, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoL, 0x20)));
            t1  = Avx2.ShiftRightLogicalVariable(a01, Unsafe.ReadUnaligned<Vector256<ulong>>(ref Unsafe.Add(ref rhoR, 0x20)));
            Vector256<ulong> p20 = Avx2.Or(t0, t1);

            // Chi
            Vector256<ulong> q7 = Avx2.ShiftRightLogical128BitLane(p01, 8);
            Vector256<ulong> q0 = Avx2.AndNot(p01, q7); // (~p01) & q7

            // A31/A41
            a31 = Avx2.Blend(p20.AsUInt32(), p11.AsUInt32(), 0x0C).AsUInt64();
            Vector256<ulong> q8 = Avx2.Blend(p21.AsUInt32(), p20.AsUInt32(), 0x0C).AsUInt64();
            a41 = Avx2.Blend(p31.AsUInt32(), p21.AsUInt32(), 0x0C).AsUInt64();
            q7  = Avx2.Blend(p20.AsUInt32(), p31.AsUInt32(), 0x0C).AsUInt64();

            a31 = Avx2.Blend(a31.AsUInt32(), p21.AsUInt32(), 0x30).AsUInt64();
            q8  = Avx2.Blend(q8.AsUInt32(),  p41.AsUInt32(), 0x30).AsUInt64();
            a41 = Avx2.Blend(a41.AsUInt32(), p20.AsUInt32(), 0x30).AsUInt64();
            q7  = Avx2.Blend(q7.AsUInt32(),  p11.AsUInt32(), 0x30).AsUInt64();

            a31 = Avx2.Blend(a31.AsUInt32(), p41.AsUInt32(), 0xC0).AsUInt64();
            q8  = Avx2.Blend(q8.AsUInt32(),  p11.AsUInt32(), 0xC0).AsUInt64();
            a41 = Avx2.Blend(a41.AsUInt32(), p11.AsUInt32(), 0xC0).AsUInt64();
            q7  = Avx2.Blend(q7.AsUInt32(),  p21.AsUInt32(), 0xC0).AsUInt64();

            a31 = Avx2.AndNot(a31, q8);
            a41 = Avx2.AndNot(a41, q7);

            // A11
            a11 = Avx2.Blend(p41.AsUInt32(), p20.AsUInt32(), 0x0C).AsUInt64();
            q8  = Avx2.Blend(p31.AsUInt32(), p41.AsUInt32(), 0x0C).AsUInt64();
            a31 = Avx2.Xor(p31, a31);

            a11 = Avx2.Blend(a11.AsUInt32(), p31.AsUInt32(), 0x30).AsUInt64();
            q8  = Avx2.Blend(q8.AsUInt32(),  p21.AsUInt32(), 0x30).AsUInt64();
            a41 = Avx2.Xor(p41, a41);

            a11 = Avx2.Blend(a11.AsUInt32(), p21.AsUInt32(), 0xC0).AsUInt64();
            q8  = Avx2.Blend(q8.AsUInt32(),  p20.AsUInt32(), 0xC0).AsUInt64();

            a11 = Avx2.AndNot(a11, q8);
            a11 = Avx2.Xor(p11, a11);

            // A01 (row0)
            a21 = Avx2.Permute4x64(p01, 0x1E);
            q8  = Avx2.Blend(a21.AsUInt32(), a00.AsUInt32(), 0x30).AsUInt64();
            a01 = Avx2.Permute4x64(p01, 0x39);
            a01 = Avx2.Blend(a01.AsUInt32(), a00.AsUInt32(), 0xC0).AsUInt64();
            a01 = Avx2.AndNot(a01, q8);

            // A20
            a20 = Avx2.Blend(p21.AsUInt32(), p41.AsUInt32(), 0x0C).AsUInt64();
            q7  = Avx2.Blend(p11.AsUInt32(), p21.AsUInt32(), 0x0C).AsUInt64();

            a20 = Avx2.Blend(a20.AsUInt32(), p11.AsUInt32(), 0x30).AsUInt64();
            q7  = Avx2.Blend(q7.AsUInt32(),  p31.AsUInt32(), 0x30).AsUInt64();

            a20 = Avx2.Blend(a20.AsUInt32(), p31.AsUInt32(), 0xC0).AsUInt64();
            q7  = Avx2.Blend(q7.AsUInt32(),  p41.AsUInt32(), 0xC0).AsUInt64();

            a20 = Avx2.AndNot(a20, q7);
            a20 = Avx2.Xor(p20, a20);

            // post-Chi shuffle
            q0  = Avx2.Permute4x64(q0,  0x00);
            a31 = Avx2.Permute4x64(a31, 0x1B);
            a41 = Avx2.Permute4x64(a41, 0x8D);
            a11 = Avx2.Permute4x64(a11, 0x72);

            // A21
            a21 = Avx2.Blend(p11.AsUInt32(), p31.AsUInt32(), 0x0C).AsUInt64();
            q7  = Avx2.Blend(p41.AsUInt32(), p11.AsUInt32(), 0x0C).AsUInt64();

            a21 = Avx2.Blend(a21.AsUInt32(), p41.AsUInt32(), 0x30).AsUInt64();
            q7  = Avx2.Blend(q7.AsUInt32(),  p20.AsUInt32(), 0x30).AsUInt64();

            a21 = Avx2.Blend(a21.AsUInt32(), p20.AsUInt32(), 0xC0).AsUInt64();
            q7  = Avx2.Blend(q7.AsUInt32(),  p31.AsUInt32(), 0xC0).AsUInt64();

            a21 = Avx2.AndNot(a21, q7);

            // final xors + iota
            a00 = Avx2.Xor(q0,  a00);
            a01 = Avx2.Xor(p01, a01);
            a21 = Avx2.Xor(p21, a21);

            // Iota as single vpxor ymm, ymm, m256 (no broadcast temp)
            a00 = Avx2.Xor(a00, Unsafe.ReadUnaligned<Vector256<ulong>>(ref iota));
            iota = ref Unsafe.Add(ref iota, 32);
        }

        // Store back to canonical (x + 5*y)
        s = a00.GetElement(0);
        Unsafe.WriteUnaligned(ref Unsafe.As<ulong, byte>(ref Unsafe.Add(ref s, 1)), a01);

        Unsafe.Add(ref s, 5)  = a20.GetElement(2);
        Unsafe.Add(ref s, 6)  = a11.GetElement(0);
        Unsafe.Add(ref s, 7)  = a31.GetElement(1);
        Unsafe.Add(ref s, 8)  = a21.GetElement(2);
        Unsafe.Add(ref s, 9)  = a41.GetElement(3);

        Unsafe.Add(ref s, 10) = a20.GetElement(0);
        Unsafe.Add(ref s, 11) = a21.GetElement(0);
        Unsafe.Add(ref s, 12) = a11.GetElement(1);
        Unsafe.Add(ref s, 13) = a41.GetElement(2);
        Unsafe.Add(ref s, 14) = a31.GetElement(3);

        Unsafe.Add(ref s, 15) = a20.GetElement(3);
        Unsafe.Add(ref s, 16) = a31.GetElement(0);
        Unsafe.Add(ref s, 17) = a41.GetElement(1);
        Unsafe.Add(ref s, 18) = a11.GetElement(2);
        Unsafe.Add(ref s, 19) = a21.GetElement(3);

        Unsafe.Add(ref s, 20) = a20.GetElement(1);
        Unsafe.Add(ref s, 21) = a41.GetElement(0);
        Unsafe.Add(ref s, 22) = a21.GetElement(1);
        Unsafe.Add(ref s, 23) = a31.GetElement(2);
        Unsafe.Add(ref s, 24) = a11.GetElement(3);
    }
}
