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
        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        ref ulong state = ref MemoryMarshal.GetReference(st);
        if (Avx512F.IsSupported)
        {
            KeccakF1600Avx512F(ref roundConstants, ref state);
        }
        //else if (Avx2.IsSupported)
        //{
        //    // Not good yet
        //    KeccakF1600Avx2(st);
        //}
        else
        {
            KeccakF1600(ref roundConstants, ref state);
        }
    }

    private static void KeccakF1600(ref ulong roundConstants, ref ulong state)
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

        aba = Unsafe.Add(ref state, 0);
        abe = Unsafe.Add(ref state, 1);
        abi = Unsafe.Add(ref state, 2);
        abo = Unsafe.Add(ref state, 3);
        abu = Unsafe.Add(ref state, 4);
        aga = Unsafe.Add(ref state, 5);
        age = Unsafe.Add(ref state, 6);
        agi = Unsafe.Add(ref state, 7);
        ago = Unsafe.Add(ref state, 8);
        agu = Unsafe.Add(ref state, 9);
        aka = Unsafe.Add(ref state, 10);
        ake = Unsafe.Add(ref state, 11);
        aki = Unsafe.Add(ref state, 12);
        ako = Unsafe.Add(ref state, 13);
        aku = Unsafe.Add(ref state, 14);
        ama = Unsafe.Add(ref state, 15);
        ame = Unsafe.Add(ref state, 16);
        ami = Unsafe.Add(ref state, 17);
        amo = Unsafe.Add(ref state, 18);
        amu = Unsafe.Add(ref state, 19);
        asa = Unsafe.Add(ref state, 20);
        ase = Unsafe.Add(ref state, 21);
        asi = Unsafe.Add(ref state, 22);
        aso = Unsafe.Add(ref state, 23);
        asu = Unsafe.Add(ref state, 24);
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
            eba = bCa ^ ((~bCe) & bCi) ^ Unsafe.Add(ref roundConstants, round);
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
            aba = bCa ^ ((~bCe) & bCi) ^ Unsafe.Add(ref roundConstants, round + 1);
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
        Unsafe.Add(ref state, 0) = aba;
        Unsafe.Add(ref state, 1) = abe;
        Unsafe.Add(ref state, 2) = abi;
        Unsafe.Add(ref state, 3) = abo;
        Unsafe.Add(ref state, 4) = abu;
        Unsafe.Add(ref state, 5) = aga;
        Unsafe.Add(ref state, 6) = age;
        Unsafe.Add(ref state, 7) = agi;
        Unsafe.Add(ref state, 8) = ago;
        Unsafe.Add(ref state, 9) = agu;
        Unsafe.Add(ref state, 10) = aka;
        Unsafe.Add(ref state, 11) = ake;
        Unsafe.Add(ref state, 12) = aki;
        Unsafe.Add(ref state, 13) = ako;
        Unsafe.Add(ref state, 14) = aku;
        Unsafe.Add(ref state, 15) = ama;
        Unsafe.Add(ref state, 16) = ame;
        Unsafe.Add(ref state, 17) = ami;
        Unsafe.Add(ref state, 18) = amo;
        Unsafe.Add(ref state, 19) = amu;
        Unsafe.Add(ref state, 20) = asa;
        Unsafe.Add(ref state, 21) = ase;
        Unsafe.Add(ref state, 22) = asi;
        Unsafe.Add(ref state, 23) = aso;
        Unsafe.Add(ref state, 24) = asu;
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
        Avx512
    }

    internal static void BenchmarkHash(ReadOnlySpan<byte> input, Span<byte> output, Implementation implementation)
    {
        int roundSize = GetRoundSize(output.Length);
        if (output.Length <= 0 || output.Length > STATE_SIZE)
        {
            ThrowBadKeccak();
        }

        Span<ulong> stateSpan = stackalloc ulong[STATE_SIZE / sizeof(ulong)];
        Span<byte> stateBytes = MemoryMarshal.AsBytes(stateSpan);
        ref ulong state = ref MemoryMarshal.GetReference(stateSpan);
        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);

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
                        KeccakF1600Avx512F(ref roundConstants, ref state);
                        break;
                    default:
                        KeccakF1600(ref roundConstants, ref state);
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
                KeccakF1600Avx512F(ref roundConstants, ref state);
                break;
            default:
                KeccakF1600(ref roundConstants, ref state);
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
    private static void KeccakF1600Avx512F(ref ulong roundConstants, ref ulong state)
    {
        // State layout:
        // - Each zmm holds one Keccak row (y fixed, x varies) in lanes 0-4.
        // - Lanes 5-7 are treated as "dead" and must never be permuted into lanes 0-4.
        Vector512<ulong> c0 = Unsafe.As<ulong, Vector512<ulong>>(ref state);
        Vector512<ulong> c1 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref state, 5));
        Vector512<ulong> c2 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref state, 10));
        Vector512<ulong> c3 = Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref state, 15));

        // Safe tail load for row4 (20..24) without over-read.
        Vector256<ulong> c4lo = Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref state, 20));
        Vector256<ulong> c4hi = Vector256.Create(Unsafe.Add(ref state, 24), 0UL, 0UL, 0UL);
        Vector512<ulong> c4 = Avx512F.InsertVector256(Vector512<ulong>.Zero, c4lo, 0);
        c4 = Avx512F.InsertVector256(c4, c4hi, 1);

        // Theta lane rotates - rotate only lanes 0-4, keep lanes 5-7 fixed.
        Vector512<ulong> rot4 = Vector512.Create(4UL, 0UL, 1UL, 2UL, 3UL, 5UL, 6UL, 7UL);
        Vector512<ulong> rot1 = Vector512.Create(1UL, 2UL, 3UL, 4UL, 0UL, 5UL, 6UL, 7UL);
        Vector512<ulong> rot2 = Vector512.Create(2UL, 3UL, 4UL, 0UL, 1UL, 5UL, 6UL, 7UL);
        Vector512<ulong> rot3 = Vector512.Create(3UL, 4UL, 0UL, 1UL, 2UL, 5UL, 6UL, 7UL);

        // Pi-to-columns indices:
        Vector512<ulong> pi0 = Vector512.Create(0UL, 3UL, 1UL, 4UL, 2UL, 5UL, 6UL, 7UL);
        Vector512<ulong> pi1 = Vector512.Create(1UL, 4UL, 2UL, 0UL, 3UL, 5UL, 6UL, 7UL);
        Vector512<ulong> pi2 = Vector512.Create(2UL, 0UL, 3UL, 1UL, 4UL, 5UL, 6UL, 7UL);
        Vector512<ulong> pi3 = Vector512.Create(3UL, 1UL, 4UL, 2UL, 0UL, 5UL, 6UL, 7UL);
        Vector512<ulong> pi4 = Vector512.Create(4UL, 2UL, 0UL, 3UL, 1UL, 5UL, 6UL, 7UL);

        // Rho counts pre-permuted by Pi-to-columns
        Vector512<ulong> rho0Pi = Vector512.Create(0UL, 28UL, 1UL, 27UL, 62UL, 0UL, 0UL, 0UL);
        Vector512<ulong> rho1Pi = Vector512.Create(44UL, 20UL, 6UL, 36UL, 55UL, 0UL, 0UL, 0UL);
        Vector512<ulong> rho2Pi = Vector512.Create(43UL, 3UL, 25UL, 10UL, 39UL, 0UL, 0UL, 0UL);
        Vector512<ulong> rho3Pi = Vector512.Create(21UL, 45UL, 8UL, 15UL, 41UL, 0UL, 0UL, 0UL);
        Vector512<ulong> rho4Pi = Vector512.Create(14UL, 61UL, 18UL, 56UL, 2UL, 0UL, 0UL, 0UL);

        // Diagonal-layout rho counts already permuted by the diagonal->row Pi lane-rotates
        Vector512<ulong> rhod0 = Vector512.Create(0UL, 44UL, 43UL, 21UL, 14UL, 0UL, 0UL, 0UL);
        Vector512<ulong> rhod1Pi = Vector512.Create(1UL, 6UL, 25UL, 8UL, 18UL, 0UL, 0UL, 0UL);
        Vector512<ulong> rhod2Pi = Vector512.Create(62UL, 55UL, 39UL, 41UL, 2UL, 0UL, 0UL, 0UL);
        Vector512<ulong> rhod3Pi = Vector512.Create(28UL, 20UL, 3UL, 45UL, 61UL, 0UL, 0UL, 0UL);
        Vector512<ulong> rhod4Pi = Vector512.Create(27UL, 36UL, 10UL, 15UL, 56UL, 0UL, 0UL, 0UL);

        for (int i = ROUNDS / 2; i != 0; i--)
        {
            // Round 0: rows -> columns
            {
                // Theta (rows)
                Vector512<ulong> parity0 = Avx512F.Xor(c0, c1);
                Vector512<ulong> parity1 = Avx512F.Xor(c2, c3);
                Vector512<ulong> parity = Avx512F.Xor(Avx512F.Xor(parity0, parity1), c4);

                Vector512<ulong> theta1a = Avx512F.PermuteVar8x64(parity, rot1);
                Vector512<ulong> theta0 = Avx512F.PermuteVar8x64(parity, rot4);
                Vector512<ulong> theta1 = Avx512F.RotateLeft(theta1a, 1);
                Vector512<ulong> theta = Avx512F.Xor(theta0, theta1);

                // Xor theta into everything
                c0 = Avx512F.Xor(c0, theta);
                c1 = Avx512F.Xor(c1, theta);
                c2 = Avx512F.Xor(c2, theta);
                c3 = Avx512F.Xor(c3, theta);
                c4 = Avx512F.Xor(c4, theta);

                // Pi-to-columns then Rho
                c0 = Avx512F.PermuteVar8x64(c0, pi0);
                c1 = Avx512F.PermuteVar8x64(c1, pi1);
                c2 = Avx512F.PermuteVar8x64(c2, pi2);
                c0 = Avx512F.RotateLeftVariable(c0, rho0Pi);
                c3 = Avx512F.PermuteVar8x64(c3, pi3);
                c1 = Avx512F.RotateLeftVariable(c1, rho1Pi);
                c4 = Avx512F.PermuteVar8x64(c4, pi4);
                c2 = Avx512F.RotateLeftVariable(c2, rho2Pi);
                c3 = Avx512F.RotateLeftVariable(c3, rho3Pi);
                c4 = Avx512F.RotateLeftVariable(c4, rho4Pi);

                // Chi (columns, cross-register) - do in byte view to discourage AndNot+Xor -> vpternlogq
                Vector512<ulong> t0b = c0;
                Vector512<ulong> t1b = c1;

                c0 = Avx512F.Xor(c0, Avx512F.AndNot(c1, c2));
                c1 = Avx512F.Xor(c1, Avx512F.AndNot(c2, c3));
                c2 = Avx512F.Xor(c2, Avx512F.AndNot(c3, c4));
                c3 = Avx512F.Xor(c3, Avx512F.AndNot(c4, t0b));
                c4 = Avx512F.Xor(c4, Avx512F.AndNot(t0b, t1b));

                // Iota
                c0 = Avx512F.Xor(c0, Vector512.CreateScalar(roundConstants));
                roundConstants = ref Unsafe.Add(ref roundConstants, 1);
            }

            // Harmonise columns -> diagonals, then Round 1: diagonals -> rows
            {
                c1 = Avx512F.PermuteVar8x64(c1, rot1);
                c2 = Avx512F.PermuteVar8x64(c2, rot2);
                c3 = Avx512F.PermuteVar8x64(c3, rot3);
                c4 = Avx512F.PermuteVar8x64(c4, rot4);

                Vector512<ulong> z = Vector512<ulong>.Zero;

                Vector512<ulong> t01e = Avx512F.UnpackLow(c0, c1);
                Vector512<ulong> t01o = Avx512F.UnpackHigh(c0, c1);
                Vector512<ulong> t23e = Avx512F.UnpackLow(c2, c3);
                Vector512<ulong> t23o = Avx512F.UnpackHigh(c2, c3);

                Vector512<ulong> t4e = Avx512F.UnpackLow(c4, z);
                Vector512<ulong> t4o = Avx512F.UnpackHigh(c4, z);

                Vector512<ulong> u0 = Avx512F.Shuffle4x128(t01e, t23e, 0x44);
                c0 = Avx512F.Shuffle4x128(u0, t4e, 0x08); // d0
                c1 = Avx512F.Shuffle4x128(u0, t4e, 0x5D); // d3

                Vector512<ulong> u1 = Avx512F.Shuffle4x128(t01o, t23o, 0x44);
                c3 = Avx512F.Shuffle4x128(u1, t4o, 0x08); // d4
                c4 = Avx512F.Shuffle4x128(u1, t4o, 0x5D); // d2

                Vector512<ulong> u4 = Avx512F.Shuffle4x128(t01e, t23e, 0xAA);
                c2 = Avx512F.Shuffle4x128(u4, t4e, 0xA8); // d1

                // Round 1 Theta (diagonals)
                Vector512<ulong> parity0 = Avx512F.Xor(c0, c1);
                Vector512<ulong> parity1 = Avx512F.Xor(c2, c3);
                Vector512<ulong> parity = Avx512F.Xor(Avx512F.Xor(parity0, parity1), c4);

                Vector512<ulong> theta1a = Avx512F.PermuteVar8x64(parity, rot1);
                Vector512<ulong> theta0 = Avx512F.PermuteVar8x64(parity, rot4);
                Vector512<ulong> theta1 = Avx512F.RotateLeft(theta1a, 1);
                Vector512<ulong> theta = Avx512F.Xor(theta0, theta1);

                // Pi (diagonals -> rows) fusion: apply theta via xors, then permute

                c1 = Avx512F.Xor(c1, theta);
                c1 = Avx512F.PermuteVar8x64(c1, rot3);

                c2 = Avx512F.Xor(c2, theta);
                c2 = Avx512F.PermuteVar8x64(c2, rot1);

                c3 = Avx512F.Xor(c3, theta);
                c3 = Avx512F.PermuteVar8x64(c3, rot4);

                c4 = Avx512F.Xor(c4, theta);
                c4 = Avx512F.PermuteVar8x64(c4, rot2);

                c0 = Avx512F.Xor(c0, theta); // d0 -> row0 (no permute)

                // Rho
                c0 = Avx512F.RotateLeftVariable(c0, rhod0);
                c1 = Avx512F.RotateLeftVariable(c1, rhod3Pi);
                c2 = Avx512F.RotateLeftVariable(c2, rhod1Pi);
                c3 = Avx512F.RotateLeftVariable(c3, rhod4Pi);
                c4 = Avx512F.RotateLeftVariable(c4, rhod2Pi);
            }

            // Chi (rows, intra-register) + Iota - replace ternlog chi with AndNot+Xor in byte view
            {
                Vector512<ulong> b0 = Avx512F.PermuteVar8x64(c0, rot1);
                Vector512<ulong> c0p = Avx512F.PermuteVar8x64(c0, rot2);

                Vector512<ulong> b1 = Avx512F.PermuteVar8x64(c1, rot1);
                Vector512<ulong> c1p = Avx512F.PermuteVar8x64(c1, rot2);

                Vector512<ulong> chiT = Avx512F.AndNot(b0, c0p);
                c0 = Avx512F.Xor(c0, chiT);

                // Iota
                c0 = Avx512F.Xor(c0, Vector512.CreateScalar(roundConstants));
                roundConstants = ref Unsafe.Add(ref roundConstants, 1);

                Vector512<ulong> b2 = Avx512F.PermuteVar8x64(c2, rot1);
                Vector512<ulong> c2p = Avx512F.PermuteVar8x64(c2, rot2);

                chiT = Avx512F.AndNot(b1, c1p);
                c1 = Avx512F.Xor(c1, chiT);

                Vector512<ulong> b3 = Avx512F.PermuteVar8x64(c3, rot1);
                Vector512<ulong> c3p = Avx512F.PermuteVar8x64(c3, rot2);

                chiT = Avx512F.AndNot(b2, c2p);
                c2 = Avx512F.Xor(c2, chiT);

                Vector512<ulong> b4 = Avx512F.PermuteVar8x64(c4, rot1);
                Vector512<ulong> c4p = Avx512F.PermuteVar8x64(c4, rot2);

                chiT = Avx512F.AndNot(b3, c3p);
                c3 = Avx512F.Xor(c3, chiT);

                chiT = Avx512F.AndNot(b4, c4p);
                c4 = Avx512F.Xor(c4, chiT);
            }
        }

        // Store rows 0-3 as full zmm; row4 as 4 lanes + scalar lane4.
        Unsafe.As<ulong, Vector512<ulong>>(ref state) = c0;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref state, 5)) = c1;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref state, 10)) = c2;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref state, 15)) = c3;

        Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref state, 20)) = c4.GetLower();
        Unsafe.Add(ref state, 24) = c4.GetElement(4);
    }
}
