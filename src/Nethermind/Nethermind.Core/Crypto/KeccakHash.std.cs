// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

using static System.Numerics.BitOperations;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    private const int LANE_BITS = 8 * 8;
    private const int TEMP_BUFF_SIZE = 144;

    /// <inheritdoc cref="KeccakHash.AbsorbMessageIntoZeroState" />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial ReadOnlySpan<byte> AbsorbMessageIntoZeroState(scoped Span<ulong> state, scoped Span<byte> stateBytes, ReadOnlySpan<byte> input, int roundSize)
    {
        // Held here rather than in the guest arm, which cannot run on a host: this is the arm a Debug test
        // run executes, so it is the one that can catch a caller the guest arm would then hash wrongly.
        Debug.Assert(!stateBytes.ContainsAnyExcept((byte)0), "the guest arm writes the first block, not XORs it");

        return AbsorbFullBlocks(state, stateBytes, input, roundSize);
    }

    // update the state with given number of rounds
    private static partial void KeccakF(Span<ulong> st)
    {
        Debug.Assert(st.Length == STATE_LANES);

        ref ulong state = ref MemoryMarshal.GetReference(st);
        if (Avx512F.VL.IsSupported)
            KeccakF1600Avx512VL(ref state);
        else
            KeccakF1600Scalar(ref state);
    }

    /// <summary>Portable Keccak-f[1600] permutation.</summary>
    /// <param name="state">Lane 0 of a 25-lane state; all 25 lanes are read and written.</param>
    internal static void KeccakF1600Scalar(ref ulong state)
    {
        Span<ulong> st = MemoryMarshal.CreateSpan(ref state, STATE_LANES);

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
            // prepareTheta
            bCa = aba ^ aga ^ aka ^ ama ^ asa;
            bCe = abe ^ age ^ ake ^ ame ^ ase;
            bCi = abi ^ agi ^ aki ^ ami ^ asi;
            bCo = abo ^ ago ^ ako ^ amo ^ aso;
            bCu = abu ^ agu ^ aku ^ amu ^ asu;

            // thetaRhoPiChiIotaPrepareTheta(round  , A, E)
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

            // prepareTheta
            bCe = ebe ^ ege ^ eke ^ eme ^ ese;
            bCu = ebu ^ egu ^ eku ^ emu ^ esu;

            // thetaRhoPiChiIotaPrepareTheta(round+1, E, A)
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

        // copyToState(state, A)
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
}
