// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Nethermind.Core.Crypto;

/// <summary>
/// Computes Keccak-256 over several independent inputs at once ("horizontal" batching): the i-th 64-bit
/// word of <see cref="BatchSize"/> independent sponge states is packed into one <see cref="Vector{T}"/>
/// lane, so a single permutation advances all states together.
/// </summary>
/// <remarks>
/// The permutation is expressed over <see cref="Vector{UInt64}"/>, whose width follows the hardware
/// (2 lanes on Arm NEON / x86 SSE2, 4 on AVX2, 8 on AVX-512 when 512-bit <c>Vector&lt;T&gt;</c> is enabled),
/// so the same code is hardware-accelerated everywhere and needs no cross-lane shuffles — unlike the
/// single-instance vertical vectorization in <see cref="KeccakHash"/>. It is a 1:1 translation of that
/// type's unrolled scalar <c>KeccakF1600</c>, with the state held in registers across all 24 rounds and a
/// per-thread reusable buffer so steady-state hashing is allocation-free. The intended use is hashing the
/// many independent trie nodes produced during a state-root commit; callers batch nodes of equal length
/// (or equal rate-block count) and fall back to <see cref="KeccakHash"/> for the ragged remainder.
/// Output is byte-identical to <see cref="KeccakHash.ComputeHash(ReadOnlySpan{byte}, Span{byte})"/>.
/// The throughput win over running the scalar path N times only materialises on wide vectors (AVX-512);
/// at 2 lanes it is slower than the heavily optimised scalar path, so callers must gate on width.
/// </remarks>
public static class KeccakHashBatch
{
    private const int Rounds = 24;
    private const int RateBytes = 136; // Keccak-256 rate (1088 bits)
    private const int RateWords = RateBytes / sizeof(ulong); // 17
    private const int HashBytes = 32;
    private const int StateWords = 25;
    private const int MaxLanes = 8; // widest Vector<ulong> (AVX-512)

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

    [ThreadStatic] private static Vector<ulong>[]? _stateScratch;

    /// <summary>Number of inputs hashed per batch on the current hardware (<c>Vector&lt;ulong&gt;.Count</c>).</summary>
    public static int BatchSize => Vector<ulong>.Count;

    /// <summary>
    /// Computes Keccak-256 of <see cref="BatchSize"/> equal-length inputs packed contiguously
    /// (lane k occupies <paramref name="inputs"/>[k*<paramref name="inputLength"/> ..]) and writes the
    /// <see cref="BatchSize"/> 32-byte digests contiguously to <paramref name="outputs"/>.
    /// </summary>
    public static void ComputeHash256(ReadOnlySpan<byte> inputs, int inputLength, Span<byte> outputs)
    {
        int lanes = Vector<ulong>.Count;
        if (inputLength < 0) throw new ArgumentOutOfRangeException(nameof(inputLength));
        if (inputs.Length < lanes * inputLength) throw new ArgumentException("Input span too small for the batch.", nameof(inputs));
        if (outputs.Length < lanes * HashBytes) throw new ArgumentException("Output span too small for the batch.", nameof(outputs));

        Vector<ulong>[] state = _stateScratch ??= new Vector<ulong>[StateWords];
        Array.Clear(state, 0, StateWords);
        Span<ulong> laneWords = stackalloc ulong[MaxLanes];

        int fullBlocks = inputLength / RateBytes;
        for (int b = 0; b < fullBlocks; b++)
        {
            for (int j = 0; j < RateWords; j++)
            {
                state[j] ^= GatherWord(inputs, inputLength, b * RateBytes, j, lanes, laneWords);
            }
            KeccakF1600(state);
        }

        // Final block: copy each lane's tail into a zeroed buffer and apply Keccak pad10*1
        // (0x01 right after the data, 0x80 at the last rate byte) exactly as KeccakHash does.
        int remOffset = fullBlocks * RateBytes;
        int rem = inputLength - remOffset;
        Span<byte> finalBlocks = stackalloc byte[MaxLanes * RateBytes];
        finalBlocks = finalBlocks[..(lanes * RateBytes)];
        finalBlocks.Clear();
        for (int k = 0; k < lanes; k++)
        {
            Span<byte> laneBlock = finalBlocks.Slice(k * RateBytes, RateBytes);
            inputs.Slice(k * inputLength + remOffset, rem).CopyTo(laneBlock);
            laneBlock[rem] ^= 0x01;
            laneBlock[RateBytes - 1] ^= 0x80;
        }
        for (int j = 0; j < RateWords; j++)
        {
            state[j] ^= GatherWord(finalBlocks, RateBytes, 0, j, lanes, laneWords);
        }
        KeccakF1600(state);

        for (int j = 0; j < HashBytes / sizeof(ulong); j++)
        {
            state[j].CopyTo(laneWords);
            for (int k = 0; k < lanes; k++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(outputs.Slice(k * HashBytes + j * sizeof(ulong), sizeof(ulong)), laneWords[k]);
            }
        }
    }

    private static Vector<ulong> GatherWord(ReadOnlySpan<byte> source, int strideBytes, int blockOffset, int wordIndex, int lanes, Span<ulong> laneWords)
    {
        int offset = blockOffset + wordIndex * sizeof(ulong);
        for (int k = 0; k < lanes; k++)
        {
            laneWords[k] = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(k * strideBytes + offset, sizeof(ulong)));
        }
        return new Vector<ulong>(laneWords);
    }

    private static Vector<ulong> Rol(Vector<ulong> x, int r) =>
        Vector.ShiftLeft(x, r) | Vector.ShiftRightLogical(x, 64 - r);

    private static Vector<ulong> AndNot(Vector<ulong> b, Vector<ulong> a) => Vector.AndNot(b, a); // (~a) & b

    /// <summary>
    /// Keccak-f[1600] applied in lockstep to <c>Vector&lt;ulong&gt;.Count</c> independent states. Holds the
    /// 25 state words in registers across all rounds; a 1:1 translation of <see cref="KeccakHash"/>'s
    /// unrolled double-round scalar permutation.
    /// </summary>
    private static void KeccakF1600(Vector<ulong>[] st)
    {
        Vector<ulong> aba, abe, abi, abo, abu;
        Vector<ulong> aga, age, agi, ago, agu;
        Vector<ulong> aka, ake, aki, ako, aku;
        Vector<ulong> ama, ame, ami, amo, amu;
        Vector<ulong> asa, ase, asi, aso, asu;
        Vector<ulong> bCa, bCe, bCi, bCo, bCu;
        Vector<ulong> da, de, di, @do, du;
        Vector<ulong> eba, ebe, ebi, ebo, ebu;
        Vector<ulong> ega, ege, egi, ego, egu;
        Vector<ulong> eka, eke, eki, eko, eku;
        Vector<ulong> ema, eme, emi, emo, emu;
        Vector<ulong> esa, ese, esi, eso, esu;

        aba = st[0]; abe = st[1]; abi = st[2]; abo = st[3]; abu = st[4];
        aga = st[5]; age = st[6]; agi = st[7]; ago = st[8]; agu = st[9];
        aka = st[10]; ake = st[11]; aki = st[12]; ako = st[13]; aku = st[14];
        ama = st[15]; ame = st[16]; ami = st[17]; amo = st[18]; amu = st[19];
        asa = st[20]; ase = st[21]; asi = st[22]; aso = st[23]; asu = st[24];

        for (int round = 0; round < Rounds; round += 2)
        {
            Vector<ulong> rc0 = new(RoundConstants[round]);
            Vector<ulong> rc1 = new(RoundConstants[round + 1]);

            // Round: A -> E
            bCa = aba ^ aga ^ aka ^ ama ^ asa;
            bCe = abe ^ age ^ ake ^ ame ^ ase;
            bCi = abi ^ agi ^ aki ^ ami ^ asi;
            bCo = abo ^ ago ^ ako ^ amo ^ aso;
            bCu = abu ^ agu ^ aku ^ amu ^ asu;

            da = bCu ^ Rol(bCe, 1);
            de = bCa ^ Rol(bCi, 1);
            di = bCe ^ Rol(bCo, 1);
            @do = bCi ^ Rol(bCu, 1);
            du = bCo ^ Rol(bCa, 1);

            bCa = aba ^ da;
            bCe = Rol(age ^ de, 44);
            bCi = Rol(aki ^ di, 43);
            eba = bCa ^ AndNot(bCi, bCe) ^ rc0;
            bCo = Rol(amo ^ @do, 21);
            ebe = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(asu ^ du, 14);
            ebi = bCi ^ AndNot(bCu, bCo);
            ebo = bCo ^ AndNot(bCa, bCu);
            ebu = bCu ^ AndNot(bCe, bCa);

            bCa = Rol(abo ^ @do, 28);
            bCe = Rol(agu ^ du, 20);
            bCi = Rol(aka ^ da, 3);
            ega = bCa ^ AndNot(bCi, bCe);
            bCo = Rol(ame ^ de, 45);
            ege = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(asi ^ di, 61);
            egi = bCi ^ AndNot(bCu, bCo);
            ego = bCo ^ AndNot(bCa, bCu);
            egu = bCu ^ AndNot(bCe, bCa);

            bCa = Rol(abe ^ de, 1);
            bCe = Rol(agi ^ di, 6);
            bCi = Rol(ako ^ @do, 25);
            eka = bCa ^ AndNot(bCi, bCe);
            bCo = Rol(amu ^ du, 8);
            eke = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(asa ^ da, 18);
            eki = bCi ^ AndNot(bCu, bCo);
            eko = bCo ^ AndNot(bCa, bCu);
            eku = bCu ^ AndNot(bCe, bCa);

            bCa = Rol(abu ^ du, 27);
            bCe = Rol(aga ^ da, 36);
            bCi = Rol(ake ^ de, 10);
            ema = bCa ^ AndNot(bCi, bCe);
            bCo = Rol(ami ^ di, 15);
            eme = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(aso ^ @do, 56);
            emi = bCi ^ AndNot(bCu, bCo);
            emo = bCo ^ AndNot(bCa, bCu);
            emu = bCu ^ AndNot(bCe, bCa);

            bCa = Rol(abi ^ di, 62);
            bCe = Rol(ago ^ @do, 55);
            bCi = Rol(aku ^ du, 39);
            esa = bCa ^ AndNot(bCi, bCe);
            bCo = Rol(ama ^ da, 41);
            ese = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(ase ^ de, 2);
            esi = bCi ^ AndNot(bCu, bCo);
            eso = bCo ^ AndNot(bCa, bCu);
            esu = bCu ^ AndNot(bCe, bCa);

            // Round: E -> A
            bCa = eba ^ ega ^ eka ^ ema ^ esa;
            bCe = ebe ^ ege ^ eke ^ eme ^ ese;
            bCi = ebi ^ egi ^ eki ^ emi ^ esi;
            bCo = ebo ^ ego ^ eko ^ emo ^ eso;
            bCu = ebu ^ egu ^ eku ^ emu ^ esu;

            da = bCu ^ Rol(bCe, 1);
            de = bCa ^ Rol(bCi, 1);
            di = bCe ^ Rol(bCo, 1);
            @do = bCi ^ Rol(bCu, 1);
            du = bCo ^ Rol(bCa, 1);

            bCa = eba ^ da;
            bCe = Rol(ege ^ de, 44);
            bCi = Rol(eki ^ di, 43);
            aba = bCa ^ AndNot(bCi, bCe) ^ rc1;
            bCo = Rol(emo ^ @do, 21);
            abe = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(esu ^ du, 14);
            abi = bCi ^ AndNot(bCu, bCo);
            abo = bCo ^ AndNot(bCa, bCu);
            abu = bCu ^ AndNot(bCe, bCa);

            bCa = Rol(ebo ^ @do, 28);
            bCe = Rol(egu ^ du, 20);
            bCi = Rol(eka ^ da, 3);
            aga = bCa ^ AndNot(bCi, bCe);
            bCo = Rol(eme ^ de, 45);
            age = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(esi ^ di, 61);
            agi = bCi ^ AndNot(bCu, bCo);
            ago = bCo ^ AndNot(bCa, bCu);
            agu = bCu ^ AndNot(bCe, bCa);

            bCa = Rol(ebe ^ de, 1);
            bCe = Rol(egi ^ di, 6);
            bCi = Rol(eko ^ @do, 25);
            aka = bCa ^ AndNot(bCi, bCe);
            bCo = Rol(emu ^ du, 8);
            ake = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(esa ^ da, 18);
            aki = bCi ^ AndNot(bCu, bCo);
            ako = bCo ^ AndNot(bCa, bCu);
            aku = bCu ^ AndNot(bCe, bCa);

            bCa = Rol(ebu ^ du, 27);
            bCe = Rol(ega ^ da, 36);
            bCi = Rol(eke ^ de, 10);
            ama = bCa ^ AndNot(bCi, bCe);
            bCo = Rol(emi ^ di, 15);
            ame = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(eso ^ @do, 56);
            ami = bCi ^ AndNot(bCu, bCo);
            amo = bCo ^ AndNot(bCa, bCu);
            amu = bCu ^ AndNot(bCe, bCa);

            bCa = Rol(ebi ^ di, 62);
            bCe = Rol(ego ^ @do, 55);
            bCi = Rol(eku ^ du, 39);
            asa = bCa ^ AndNot(bCi, bCe);
            bCo = Rol(ema ^ da, 41);
            ase = bCe ^ AndNot(bCo, bCi);
            bCu = Rol(ese ^ de, 2);
            asi = bCi ^ AndNot(bCu, bCo);
            aso = bCo ^ AndNot(bCa, bCu);
            asu = bCu ^ AndNot(bCe, bCa);
        }

        st[0] = aba; st[1] = abe; st[2] = abi; st[3] = abo; st[4] = abu;
        st[5] = aga; st[6] = age; st[7] = agi; st[8] = ago; st[9] = agu;
        st[10] = aka; st[11] = ake; st[12] = aki; st[13] = ako; st[14] = aku;
        st[15] = ama; st[16] = ame; st[17] = ami; st[18] = amo; st[19] = amu;
        st[20] = asa; st[21] = ase; st[22] = asi; st[23] = aso; st[24] = asu;
    }
}
