// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    [SkipLocalsInit]
    internal static unsafe void KeccakF1600ArmSha3(Span<ulong> st)
    {
        Debug.Assert(st.Length == STATE_SIZE / sizeof(ulong));

        Vector128<ulong> aba, abe, abi, abo, abu;
        Vector128<ulong> aga, age, agi, ago, agu;
        Vector128<ulong> aka, ake, aki, ako, aku;
        Vector128<ulong> ama, ame, ami, amo, amu;
        Vector128<ulong> asa, ase, asi, aso, asu;
        Vector128<ulong> bCa, bCe, bCi, bCo, bCu;
        Vector128<ulong> da, de, di, @do, du;
        Vector128<ulong> eba, ebe, ebi, ebo, ebu;
        Vector128<ulong> ega, ege, egi, ego, egu;
        Vector128<ulong> eka, eke, eki, eko, eku;
        Vector128<ulong> ema, eme, emi, emo, emu;
        Vector128<ulong> esa, ese, esi, eso, esu;

        ref ulong state = ref MemoryMarshal.GetReference(st);
        ref ulong roundConstants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);

        asu = Vector64.LoadUnsafe(ref state, 24).ToVector128Unsafe();
        aso = Vector64.LoadUnsafe(ref state, 23).ToVector128Unsafe();
        asi = Vector64.LoadUnsafe(ref state, 22).ToVector128Unsafe();
        ase = Vector64.LoadUnsafe(ref state, 21).ToVector128Unsafe();
        asa = Vector64.LoadUnsafe(ref state, 20).ToVector128Unsafe();
        amu = Vector64.LoadUnsafe(ref state, 19).ToVector128Unsafe();
        amo = Vector64.LoadUnsafe(ref state, 18).ToVector128Unsafe();
        ami = Vector64.LoadUnsafe(ref state, 17).ToVector128Unsafe();
        ame = Vector64.LoadUnsafe(ref state, 16).ToVector128Unsafe();
        ama = Vector64.LoadUnsafe(ref state, 15).ToVector128Unsafe();
        aku = Vector64.LoadUnsafe(ref state, 14).ToVector128Unsafe();
        ako = Vector64.LoadUnsafe(ref state, 13).ToVector128Unsafe();
        aki = Vector64.LoadUnsafe(ref state, 12).ToVector128Unsafe();
        ake = Vector64.LoadUnsafe(ref state, 11).ToVector128Unsafe();
        aka = Vector64.LoadUnsafe(ref state, 10).ToVector128Unsafe();
        agu = Vector64.LoadUnsafe(ref state, 9).ToVector128Unsafe();
        ago = Vector64.LoadUnsafe(ref state, 8).ToVector128Unsafe();
        agi = Vector64.LoadUnsafe(ref state, 7).ToVector128Unsafe();
        age = Vector64.LoadUnsafe(ref state, 6).ToVector128Unsafe();
        aga = Vector64.LoadUnsafe(ref state, 5).ToVector128Unsafe();
        abu = Vector64.LoadUnsafe(ref state, 4).ToVector128Unsafe();
        abo = Vector64.LoadUnsafe(ref state, 3).ToVector128Unsafe();
        abi = Vector64.LoadUnsafe(ref state, 2).ToVector128Unsafe();
        abe = Vector64.LoadUnsafe(ref state, 1).ToVector128Unsafe();
        aba = Vector64.LoadUnsafe(ref state).ToVector128Unsafe();

        for (int round = 0; round < ROUNDS; round += 2)
        {
            bCa = Sha3.Xor(Sha3.Xor(aba, aga, aka), ama, asa);
            bCe = Sha3.Xor(Sha3.Xor(abe, age, ake), ame, ase);
            bCi = Sha3.Xor(Sha3.Xor(abi, agi, aki), ami, asi);
            bCo = Sha3.Xor(Sha3.Xor(abo, ago, ako), amo, aso);
            bCu = Sha3.Xor(Sha3.Xor(abu, agu, aku), amu, asu);

            da = Sha3.BitwiseRotateLeftBy1AndXor(bCu, bCe);
            de = Sha3.BitwiseRotateLeftBy1AndXor(bCa, bCi);
            di = Sha3.BitwiseRotateLeftBy1AndXor(bCe, bCo);
            @do = Sha3.BitwiseRotateLeftBy1AndXor(bCi, bCu);
            du = Sha3.BitwiseRotateLeftBy1AndXor(bCo, bCa);

            bCa = Vector128.Xor(aba, da);
            bCe = Sha3.XorRotateRight(age, de, 20);
            bCi = Sha3.XorRotateRight(aki, di, 21);
            eba = Vector128.Xor(Sha3.BitwiseClearXor(bCa, bCi, bCe), Vector64.LoadUnsafe(ref roundConstants, (nuint)round).ToVector128Unsafe());
            bCo = Sha3.XorRotateRight(amo, @do, 43);
            ebe = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(asu, du, 50);
            ebi = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            ebo = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            ebu = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sha3.XorRotateRight(abo, @do, 36);
            bCe = Sha3.XorRotateRight(agu, du, 44);
            bCi = Sha3.XorRotateRight(aka, da, 61);
            ega = Sha3.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sha3.XorRotateRight(ame, de, 19);
            ege = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(asi, di, 3);
            egi = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            ego = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            egu = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sha3.XorRotateRight(abe, de, 63);
            bCe = Sha3.XorRotateRight(agi, di, 58);
            bCi = Sha3.XorRotateRight(ako, @do, 39);
            eka = Sha3.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sha3.XorRotateRight(amu, du, 56);
            eke = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(asa, da, 46);
            eki = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            eko = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            eku = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sha3.XorRotateRight(abu, du, 37);
            bCe = Sha3.XorRotateRight(aga, da, 28);
            bCi = Sha3.XorRotateRight(ake, de, 54);
            ema = Sha3.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sha3.XorRotateRight(ami, di, 49);
            eme = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(aso, @do, 8);
            emi = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            emo = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            emu = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sha3.XorRotateRight(abi, di, 2);
            bCe = Sha3.XorRotateRight(ago, @do, 9);
            bCi = Sha3.XorRotateRight(aku, du, 25);
            esa = Sha3.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sha3.XorRotateRight(ama, da, 23);
            ese = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(ase, de, 62);
            esi = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            eso = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            esu = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCe = Sha3.Xor(Sha3.Xor(ebe, ege, eke), eme, ese);
            bCu = Sha3.Xor(Sha3.Xor(ebu, egu, eku), emu, esu);

            da = Sha3.BitwiseRotateLeftBy1AndXor(bCu, bCe);
            bCa = Sha3.Xor(Sha3.Xor(eba, ega, eka), ema, esa);
            bCi = Sha3.Xor(Sha3.Xor(ebi, egi, eki), emi, esi);
            de = Sha3.BitwiseRotateLeftBy1AndXor(bCa, bCi);
            bCo = Sha3.Xor(Sha3.Xor(ebo, ego, eko), emo, eso);
            di = Sha3.BitwiseRotateLeftBy1AndXor(bCe, bCo);
            @do = Sha3.BitwiseRotateLeftBy1AndXor(bCi, bCu);
            du = Sha3.BitwiseRotateLeftBy1AndXor(bCo, bCa);

            bCi = Sha3.XorRotateRight(eki, di, 21);
            bCe = Sha3.XorRotateRight(ege, de, 20);
            bCa = Vector128.Xor(eba, da);
            aba = Vector128.Xor(Sha3.BitwiseClearXor(bCa, bCi, bCe), Vector64.LoadUnsafe(ref roundConstants, (nuint)(round + 1)).ToVector128Unsafe());
            bCo = Sha3.XorRotateRight(emo, @do, 43);
            abe = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(esu, du, 50);
            abi = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            abo = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            abu = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sha3.XorRotateRight(ebo, @do, 36);
            bCe = Sha3.XorRotateRight(egu, du, 44);
            bCi = Sha3.XorRotateRight(eka, da, 61);
            aga = Sha3.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sha3.XorRotateRight(eme, de, 19);
            age = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(esi, di, 3);
            agi = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            ago = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            agu = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sha3.XorRotateRight(ebe, de, 63);
            bCe = Sha3.XorRotateRight(egi, di, 58);
            bCi = Sha3.XorRotateRight(eko, @do, 39);
            aka = Sha3.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sha3.XorRotateRight(emu, du, 56);
            ake = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(esa, da, 46);
            aki = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            ako = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            aku = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sha3.XorRotateRight(ebu, du, 37);
            bCe = Sha3.XorRotateRight(ega, da, 28);
            bCi = Sha3.XorRotateRight(eke, de, 54);
            ama = Sha3.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sha3.XorRotateRight(emi, di, 49);
            ame = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(eso, @do, 8);
            ami = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            amo = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            amu = Sha3.BitwiseClearXor(bCu, bCe, bCa);

            bCa = Sha3.XorRotateRight(ebi, di, 2);
            bCe = Sha3.XorRotateRight(ego, @do, 9);
            bCi = Sha3.XorRotateRight(eku, du, 25);
            asa = Sha3.BitwiseClearXor(bCa, bCi, bCe);
            bCo = Sha3.XorRotateRight(ema, da, 23);
            ase = Sha3.BitwiseClearXor(bCe, bCo, bCi);
            bCu = Sha3.XorRotateRight(ese, de, 62);
            asi = Sha3.BitwiseClearXor(bCi, bCu, bCo);
            aso = Sha3.BitwiseClearXor(bCo, bCa, bCu);
            asu = Sha3.BitwiseClearXor(bCu, bCe, bCa);
        }

        fixed (ulong* destination = &state)
        {
            AdvSimd.Arm64.StorePair(destination, aba.GetLower(), abe.GetLower());
            AdvSimd.Arm64.StorePair(destination + 2, abi.GetLower(), abo.GetLower());
            AdvSimd.Arm64.StorePair(destination + 4, abu.GetLower(), aga.GetLower());
            AdvSimd.Arm64.StorePair(destination + 6, age.GetLower(), agi.GetLower());
            AdvSimd.Arm64.StorePair(destination + 8, ago.GetLower(), agu.GetLower());
            AdvSimd.Arm64.StorePair(destination + 10, aka.GetLower(), ake.GetLower());
            AdvSimd.Arm64.StorePair(destination + 12, aki.GetLower(), ako.GetLower());
            AdvSimd.Arm64.StorePair(destination + 14, aku.GetLower(), ama.GetLower());
            AdvSimd.Arm64.StorePair(destination + 16, ame.GetLower(), ami.GetLower());
            AdvSimd.Arm64.StorePair(destination + 18, amo.GetLower(), amu.GetLower());
            AdvSimd.Arm64.StorePair(destination + 20, asa.GetLower(), ase.GetLower());
            AdvSimd.Arm64.StorePair(destination + 22, asi.GetLower(), aso.GetLower());
            asu.GetLower().Store(destination + 24);
        }
    }
}
