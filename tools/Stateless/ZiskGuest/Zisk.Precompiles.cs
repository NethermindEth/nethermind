// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable CA1401 // P/Invokes should not be visible
#pragma warning disable SYSLIB1054 // Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time

using System.Runtime.InteropServices;
using static Microsoft.FSharp.Core.ByRefKinds;

namespace Nethermind.Stateless.ZiskGuest;

public static unsafe partial class Zisk
{
    public static class Precompiles
    {
        [DllImport("__Internal")]
        public static extern byte bls12_381_pairing_check_c(byte* pairs, nuint num_pairs);

        [DllImport("__Internal")]
        public static extern byte bn254_g1_add_c(byte* p1, byte* p2, byte* ret);

        [DllImport("__Internal")]
        public static extern byte bn254_g1_mul_c(byte* point, byte* scalar, byte* ret);

        [DllImport("__Internal")]
        public static extern byte bn254_pairing_check_c(byte* pairs, nuint num_pairs);

        [DllImport("__Internal")]
        public static extern nuint modexp_bytes_c(
            byte* base_ptr,
            nuint base_len,
            byte* exp_ptr, 
            nuint exp_len,
            byte* modulus_ptr,
            nuint modulus_len,
            byte* result_ptr
        );
    }
}
