// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

using static System.Numerics.BitOperations;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    private const int ROUNDS = 24;
    private const int LANE_BITS = 8 * 8;
    private const int TEMP_BUFF_SIZE = 144;
    private const ulong AT_HWCAP2 = 26;
    private const ulong HWCAP2_SVESHA3 = 1UL << 5;
    // Resolution verifies the SVE2 permutation, so RoundConstants must initialize first.
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

    internal enum ExperimentalSve2KeccakStatus
    {
        Disabled,
        Unsupported,
        VerificationFailed,
        Enabled,
    }

    private static readonly (ExperimentalSve2KeccakStatus Status, Exception? Failure) s_experimentalSve2Keccak = ResolveExperimentalSve2Keccak();

    internal static ExperimentalSve2KeccakStatus ExperimentalSve2KeccakState => s_experimentalSve2Keccak.Status;

    internal static Exception? ExperimentalSve2KeccakFailure => s_experimentalSve2Keccak.Failure;

    private static (ExperimentalSve2KeccakStatus Status, Exception? Failure) ResolveExperimentalSve2Keccak()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("NETHERMIND_EXPERIMENTAL_SVE2_KECCAK") != "1")
                return (ExperimentalSve2KeccakStatus.Disabled, null);

            if (!IsSve2KeccakSupportedCore())
                return (ExperimentalSve2KeccakStatus.Unsupported, null);

            return VerifySve2Keccak()
                ? (ExperimentalSve2KeccakStatus.Enabled, null)
                : (ExperimentalSve2KeccakStatus.VerificationFailed, null);
        }
        catch (Exception exception)
        {
            return (ExperimentalSve2KeccakStatus.VerificationFailed, exception);
        }
    }

    internal static bool IsSve2KeccakSupported()
    {
        try
        {
            return IsSve2KeccakSupportedCore();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSve2KeccakSupportedCore()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return false;

#pragma warning disable SYSLIB5003
        bool sve2Supported = Sve2.IsSupported;
#pragma warning restore SYSLIB5003
        return sve2Supported && (GetAuxiliaryValue(AT_HWCAP2) & HWCAP2_SVESHA3) != 0;
    }

    [DllImport("libc", EntryPoint = "getauxval")]
    private static extern ulong GetAuxiliaryValue(ulong type);

    // update the state with given number of rounds
    private static partial void KeccakF(Span<ulong> st)
    {
        if (s_experimentalSve2Keccak.Status == ExperimentalSve2KeccakStatus.Enabled)
        {
            KeccakF1600Sve2(st);
            return;
        }

        if (Avx512F.IsSupported)
            KeccakF1600Avx512F(st);
        else
            KeccakF1600Scalar(st);
    }

    internal static void KeccakF1600Scalar(Span<ulong> st)
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

    private static bool VerifySve2Keccak()
    {
        Span<ulong> scalarState = stackalloc ulong[25];
        Span<ulong> sveState = stackalloc ulong[25];

        scalarState.Clear();
        if (!VerifySve2KeccakState(scalarState, sveState))
            return false;

        scalarState.Fill(ulong.MaxValue);
        if (!VerifySve2KeccakState(scalarState, sveState))
            return false;

        for (int lane = 0; lane < scalarState.Length; lane++)
        {
            scalarState[lane] = (ulong)(lane + 1) * 0x9E3779B97F4A7C15UL ^ 0xD1B54A32D192ED03UL;
        }

        return VerifySve2KeccakState(scalarState, sveState);
    }

    private static bool VerifySve2KeccakState(Span<ulong> scalarState, Span<ulong> sveState)
    {
        scalarState.CopyTo(sveState);
        KeccakF1600Scalar(scalarState);
        KeccakF1600Sve2(sveState);
        return scalarState.SequenceEqual(sveState);
    }

#pragma warning disable SYSLIB5003
    [SkipLocalsInit]
    internal static void KeccakF1600Sve2(Span<ulong> st)
    {
        Debug.Assert(st.Length == 25);

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

        asu = new Vector<ulong>(st[24]);
        aso = new Vector<ulong>(st[23]);
        asi = new Vector<ulong>(st[22]);
        ase = new Vector<ulong>(st[21]);
        asa = new Vector<ulong>(st[20]);
        amu = new Vector<ulong>(st[19]);
        amo = new Vector<ulong>(st[18]);
        ami = new Vector<ulong>(st[17]);
        ame = new Vector<ulong>(st[16]);
        ama = new Vector<ulong>(st[15]);
        aku = new Vector<ulong>(st[14]);
        ako = new Vector<ulong>(st[13]);
        aki = new Vector<ulong>(st[12]);
        ake = new Vector<ulong>(st[11]);
        aka = new Vector<ulong>(st[10]);
        agu = new Vector<ulong>(st[9]);
        ago = new Vector<ulong>(st[8]);
        agi = new Vector<ulong>(st[7]);
        age = new Vector<ulong>(st[6]);
        aga = new Vector<ulong>(st[5]);
        abu = new Vector<ulong>(st[4]);
        abo = new Vector<ulong>(st[3]);
        abi = new Vector<ulong>(st[2]);
        abe = new Vector<ulong>(st[1]);
        aba = new Vector<ulong>(st[0]);

        for (int round = 0; round < ROUNDS; round += 2)
        {
            bCa = Sve2.Xor(Sve2.Xor(aba, aga, aka), ama, asa);
            bCe = Sve2.Xor(Sve2.Xor(abe, age, ake), ame, ase);
            bCi = Sve2.Xor(Sve2.Xor(abi, agi, aki), ami, asi);
            bCo = Sve2.Xor(Sve2.Xor(abo, ago, ako), amo, aso);
            bCu = Sve2.Xor(Sve2.Xor(abu, agu, aku), amu, asu);

            da = Vector.Xor(bCu, RotateLeftOne(bCe));
            de = Vector.Xor(bCa, RotateLeftOne(bCi));
            di = Vector.Xor(bCe, RotateLeftOne(bCo));
            @do = Vector.Xor(bCi, RotateLeftOne(bCu));
            du = Vector.Xor(bCo, RotateLeftOne(bCa));

            bCa = Vector.Xor(aba, da);
            bCe = Sve2.XorRotateRight(age, de, (byte)20);
            bCi = Sve2.XorRotateRight(aki, di, (byte)21);
            eba = Vector.Xor(Sve2.BitwiseClearXor(bCa, bCi, bCe), new Vector<ulong>(RoundConstants[round]));
            bCo = Sve2.XorRotateRight(amo, @do, (byte)43);
            ebe = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(asu, du, (byte)50);
            ebi = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            ebo = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            ebu = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sve2.XorRotateRight(abo, @do, (byte)36);
            bCe = Sve2.XorRotateRight(agu, du, (byte)44);
            bCi = Sve2.XorRotateRight(aka, da, (byte)61);
            ega = Sve2.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sve2.XorRotateRight(ame, de, (byte)19);
            ege = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(asi, di, (byte)3);
            egi = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            ego = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            egu = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sve2.XorRotateRight(abe, de, (byte)63);
            bCe = Sve2.XorRotateRight(agi, di, (byte)58);
            bCi = Sve2.XorRotateRight(ako, @do, (byte)39);
            eka = Sve2.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sve2.XorRotateRight(amu, du, (byte)56);
            eke = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(asa, da, (byte)46);
            eki = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            eko = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            eku = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sve2.XorRotateRight(abu, du, (byte)37);
            bCe = Sve2.XorRotateRight(aga, da, (byte)28);
            bCi = Sve2.XorRotateRight(ake, de, (byte)54);
            ema = Sve2.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sve2.XorRotateRight(ami, di, (byte)49);
            eme = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(aso, @do, (byte)8);
            emi = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            emo = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            emu = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sve2.XorRotateRight(abi, di, (byte)2);
            bCe = Sve2.XorRotateRight(ago, @do, (byte)9);
            bCi = Sve2.XorRotateRight(aku, du, (byte)25);
            esa = Sve2.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sve2.XorRotateRight(ama, da, (byte)23);
            ese = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(ase, de, (byte)62);
            esi = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            eso = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            esu = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCe = Sve2.Xor(Sve2.Xor(ebe, ege, eke), eme, ese);
            bCu = Sve2.Xor(Sve2.Xor(ebu, egu, eku), emu, esu);

            da = Vector.Xor(bCu, RotateLeftOne(bCe));
            bCa = Sve2.Xor(Sve2.Xor(eba, ega, eka), ema, esa);
            bCi = Sve2.Xor(Sve2.Xor(ebi, egi, eki), emi, esi);
            de = Vector.Xor(bCa, RotateLeftOne(bCi));
            bCo = Sve2.Xor(Sve2.Xor(ebo, ego, eko), emo, eso);
            di = Vector.Xor(bCe, RotateLeftOne(bCo));
            @do = Vector.Xor(bCi, RotateLeftOne(bCu));
            du = Vector.Xor(bCo, RotateLeftOne(bCa));

            bCi = Sve2.XorRotateRight(eki, di, (byte)21);
            bCe = Sve2.XorRotateRight(ege, de, (byte)20);
            bCa = Vector.Xor(eba, da);
            aba = Vector.Xor(Sve2.BitwiseClearXor(bCa, bCi, bCe), new Vector<ulong>(RoundConstants[round + 1]));
            bCo = Sve2.XorRotateRight(emo, @do, (byte)43);
            abe = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(esu, du, (byte)50);
            abi = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            abo = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            abu = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sve2.XorRotateRight(ebo, @do, (byte)36);
            bCe = Sve2.XorRotateRight(egu, du, (byte)44);
            bCi = Sve2.XorRotateRight(eka, da, (byte)61);
            aga = Sve2.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sve2.XorRotateRight(eme, de, (byte)19);
            age = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(esi, di, (byte)3);
            agi = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            ago = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            agu = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sve2.XorRotateRight(ebe, de, (byte)63);
            bCe = Sve2.XorRotateRight(egi, di, (byte)58);
            bCi = Sve2.XorRotateRight(eko, @do, (byte)39);
            aka = Sve2.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sve2.XorRotateRight(emu, du, (byte)56);
            ake = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(esa, da, (byte)46);
            aki = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            ako = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            aku = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sve2.XorRotateRight(ebu, du, (byte)37);
            bCe = Sve2.XorRotateRight(ega, da, (byte)28);
            bCi = Sve2.XorRotateRight(eke, de, (byte)54);
            ama = Sve2.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sve2.XorRotateRight(emi, di, (byte)49);
            ame = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(eso, @do, (byte)8);
            ami = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            amo = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            amu = Sve2.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sve2.XorRotateRight(ebi, di, (byte)2);
            bCe = Sve2.XorRotateRight(ego, @do, (byte)9);
            bCi = Sve2.XorRotateRight(eku, du, (byte)25);
            asa = Sve2.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sve2.XorRotateRight(ema, da, (byte)23);
            ase = Sve2.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sve2.XorRotateRight(ese, de, (byte)62);
            asi = Sve2.BitwiseClearXor(bCi, bCu, bCo);
            aso = Sve2.BitwiseClearXor(bCo, bCa, bCu);
            asu = Sve2.BitwiseClearXor(bCu, bCe, bCa);
        }

        st[24] = asu[0];
        st[23] = aso[0];
        st[22] = asi[0];
        st[21] = ase[0];
        st[20] = asa[0];
        st[19] = amu[0];
        st[18] = amo[0];
        st[17] = ami[0];
        st[16] = ame[0];
        st[15] = ama[0];
        st[14] = aku[0];
        st[13] = ako[0];
        st[12] = aki[0];
        st[11] = ake[0];
        st[10] = aka[0];
        st[9] = agu[0];
        st[8] = ago[0];
        st[7] = agi[0];
        st[6] = age[0];
        st[5] = aga[0];
        st[4] = abu[0];
        st[3] = abo[0];
        st[2] = abi[0];
        st[1] = abe[0];
        st[0] = aba[0];
    }
#pragma warning restore SYSLIB5003

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<ulong> RotateLeftOne(Vector<ulong> value)
        => Vector.BitwiseOr(Vector.ShiftLeft(value, 1), Vector.ShiftRightLogical(value, LANE_BITS - 1));

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

        Vector512<ulong> permute1 = Vector512.Create(1UL, 2UL, 3UL, 4UL, 0UL, 5UL, 6UL, 7UL);
        Vector512<ulong> permute2 = Vector512.Create(2UL, 3UL, 4UL, 0UL, 1UL, 5UL, 6UL, 7UL);
        ulong[] roundConstants = RoundConstants;

        // Use constant for loop so Jit expects to loop; unroll once
        for (int round = 0; round < ROUNDS; round += 2)
        {
            // Iteration 1
            {
                ulong roundConstant = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(roundConstants), round);
                // Theta step
                Vector512<ulong> parity = Avx512F.TernaryLogic(Avx512F.TernaryLogic(c0, c1, c2, 0x96), c3, c4, 0x96);

                // Compute Theta
                Vector512<ulong> bVecRot1Rotated = Avx512F.RotateLeft(Avx512F.PermuteVar8x64(parity, Vector512.Create(1UL, 2UL, 3UL, 4UL, 0UL, 5UL, 6UL, 7UL)), 1);
                Vector512<ulong> bVecRot4 = Avx512F.PermuteVar8x64(parity, Vector512.Create(4UL, 0UL, 1UL, 2UL, 3UL, 5UL, 6UL, 7UL));
                Vector512<ulong> theta = Avx512F.Xor(bVecRot4, bVecRot1Rotated);

                c0 = Avx512F.Xor(c0, theta);
                c1 = Avx512F.Xor(c1, theta);
                c2 = Avx512F.Xor(c2, theta);
                c3 = Avx512F.Xor(c3, theta);
                c4 = Avx512F.Xor(c4, theta);

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
                c0 = c0Pi;
                c1 = c1Pi;
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 8 + 4, 3, 4, 5, 6, 7), c2);
                c2 = c2Pi;
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 2, 8 + 0, 4, 5, 6, 7), c3);
                c3 = c3Pi;
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 2, 3, 8 + 1, 5, 6, 7), c4);
                c4 = c4Pi;

                // Chi step

                c0 = Avx512F.TernaryLogic(c0, Avx512F.PermuteVar8x64(c0, permute1), Avx512F.PermuteVar8x64(c0, permute2), 0xD2);
                c1 = Avx512F.TernaryLogic(c1, Avx512F.PermuteVar8x64(c1, permute1), Avx512F.PermuteVar8x64(c1, permute2), 0xD2);
                c2 = Avx512F.TernaryLogic(c2, Avx512F.PermuteVar8x64(c2, permute1), Avx512F.PermuteVar8x64(c2, permute2), 0xD2);
                c3 = Avx512F.TernaryLogic(c3, Avx512F.PermuteVar8x64(c3, permute1), Avx512F.PermuteVar8x64(c3, permute2), 0xD2);
                c4 = Avx512F.TernaryLogic(c4, Avx512F.PermuteVar8x64(c4, permute1), Avx512F.PermuteVar8x64(c4, permute2), 0xD2);

                // Iota step
                c0 = Vector512.Xor(c0, Vector512.Create(roundConstant, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL));
            }
            // Iteration 2
            {
                ulong roundConstant = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(roundConstants), round + 1);
                // Theta step
                Vector512<ulong> parity = Avx512F.TernaryLogic(Avx512F.TernaryLogic(c0, c1, c2, 0x96), c3, c4, 0x96);

                // Compute Theta
                Vector512<ulong> bVecRot1Rotated = Avx512F.RotateLeft(Avx512F.PermuteVar8x64(parity, Vector512.Create(1UL, 2UL, 3UL, 4UL, 0UL, 5UL, 6UL, 7UL)), 1);
                Vector512<ulong> bVecRot4 = Avx512F.PermuteVar8x64(parity, Vector512.Create(4UL, 0UL, 1UL, 2UL, 3UL, 5UL, 6UL, 7UL));
                Vector512<ulong> theta = Avx512F.Xor(bVecRot4, bVecRot1Rotated);

                c0 = Avx512F.Xor(c0, theta);
                c1 = Avx512F.Xor(c1, theta);
                c2 = Avx512F.Xor(c2, theta);
                c3 = Avx512F.Xor(c3, theta);
                c4 = Avx512F.Xor(c4, theta);

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
                c0 = c0Pi;
                c1 = c1Pi;
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 8 + 4, 3, 4, 5, 6, 7), c2);
                c2 = c2Pi;
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 2, 8 + 0, 4, 5, 6, 7), c3);
                c3 = c3Pi;
                c4Pi = Avx512F.PermuteVar8x64x2(c4Pi, Vector512.Create(0UL, 1, 2, 3, 8 + 1, 5, 6, 7), c4);
                c4 = c4Pi;

                // Chi step

                c0 = Avx512F.TernaryLogic(c0, Avx512F.PermuteVar8x64(c0, permute1), Avx512F.PermuteVar8x64(c0, permute2), 0xD2);
                c1 = Avx512F.TernaryLogic(c1, Avx512F.PermuteVar8x64(c1, permute1), Avx512F.PermuteVar8x64(c1, permute2), 0xD2);
                c2 = Avx512F.TernaryLogic(c2, Avx512F.PermuteVar8x64(c2, permute1), Avx512F.PermuteVar8x64(c2, permute2), 0xD2);
                c3 = Avx512F.TernaryLogic(c3, Avx512F.PermuteVar8x64(c3, permute1), Avx512F.PermuteVar8x64(c3, permute2), 0xD2);
                c4 = Avx512F.TernaryLogic(c4, Avx512F.PermuteVar8x64(c4, permute1), Avx512F.PermuteVar8x64(c4, permute2), 0xD2);

                // Iota step
                c0 = Vector512.Xor(c0, Vector512.Create(roundConstant, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL));
            }
        }

        // Can over-write for first elements
        Unsafe.As<ulong, Vector512<ulong>>(ref MemoryMarshal.GetReference(state)) = c0;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 5)) = c1;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 10)) = c2;
        Unsafe.As<ulong, Vector512<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 15)) = c3;
        // Can't over-write for last elements so write the upper Vector256 and then ulong
        Unsafe.As<ulong, Vector256<ulong>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(state), 20)) = c4.GetLower();
        state[24] = c4.GetElement(4);
    }
}
