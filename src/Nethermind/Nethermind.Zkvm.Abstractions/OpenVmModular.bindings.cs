// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static unsafe partial class OpenVmModular
{
    // The entry points openvm-algebra's moduli_init! generates for each modulus of the VM configuration, one per
    // instruction. Each pointer addresses a 32-byte little-endian value aligned to 32 bytes.

    [LibraryImport("__Internal", EntryPoint = "moduli_setup_extern_func_30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001")]
    [SuppressGCTransition]
    private static partial void ModuliSetupBn254Scalar();

    [LibraryImport("__Internal", EntryPoint = "add_extern_func_30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001")]
    [SuppressGCTransition]
    private static partial void AddBn254Scalar(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "mul_extern_func_30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001")]
    [SuppressGCTransition]
    private static partial void MulBn254Scalar(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "is_eq_extern_func_30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001")]
    [SuppressGCTransition]
    private static partial byte IsEqBn254Scalar(nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "moduli_setup_extern_func_30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47")]
    [SuppressGCTransition]
    private static partial void ModuliSetupBn254Base();

    [LibraryImport("__Internal", EntryPoint = "add_extern_func_30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47")]
    [SuppressGCTransition]
    private static partial void AddBn254Base(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "mul_extern_func_30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47")]
    [SuppressGCTransition]
    private static partial void MulBn254Base(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "is_eq_extern_func_30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47")]
    [SuppressGCTransition]
    private static partial byte IsEqBn254Base(nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "moduli_setup_extern_func_fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f")]
    [SuppressGCTransition]
    private static partial void ModuliSetupSecp256k1Base();

    [LibraryImport("__Internal", EntryPoint = "add_extern_func_fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f")]
    [SuppressGCTransition]
    private static partial void AddSecp256k1Base(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "mul_extern_func_fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f")]
    [SuppressGCTransition]
    private static partial void MulSecp256k1Base(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "is_eq_extern_func_fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f")]
    [SuppressGCTransition]
    private static partial byte IsEqSecp256k1Base(nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "moduli_setup_extern_func_fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141")]
    [SuppressGCTransition]
    private static partial void ModuliSetupSecp256k1Scalar();

    [LibraryImport("__Internal", EntryPoint = "add_extern_func_fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141")]
    [SuppressGCTransition]
    private static partial void AddSecp256k1Scalar(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "mul_extern_func_fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141")]
    [SuppressGCTransition]
    private static partial void MulSecp256k1Scalar(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "is_eq_extern_func_fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141")]
    [SuppressGCTransition]
    private static partial byte IsEqSecp256k1Scalar(nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "moduli_setup_extern_func_ffffffff00000001000000000000000000000000ffffffffffffffffffffffff")]
    [SuppressGCTransition]
    private static partial void ModuliSetupP256Base();

    [LibraryImport("__Internal", EntryPoint = "add_extern_func_ffffffff00000001000000000000000000000000ffffffffffffffffffffffff")]
    [SuppressGCTransition]
    private static partial void AddP256Base(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "mul_extern_func_ffffffff00000001000000000000000000000000ffffffffffffffffffffffff")]
    [SuppressGCTransition]
    private static partial void MulP256Base(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "is_eq_extern_func_ffffffff00000001000000000000000000000000ffffffffffffffffffffffff")]
    [SuppressGCTransition]
    private static partial byte IsEqP256Base(nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "moduli_setup_extern_func_ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551")]
    [SuppressGCTransition]
    private static partial void ModuliSetupP256Scalar();

    [LibraryImport("__Internal", EntryPoint = "add_extern_func_ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551")]
    [SuppressGCTransition]
    private static partial void AddP256Scalar(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "mul_extern_func_ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551")]
    [SuppressGCTransition]
    private static partial void MulP256Scalar(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "is_eq_extern_func_ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551")]
    [SuppressGCTransition]
    private static partial byte IsEqP256Scalar(nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "moduli_setup_extern_func_73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001")]
    [SuppressGCTransition]
    private static partial void ModuliSetupBls12381Scalar();

    [LibraryImport("__Internal", EntryPoint = "add_extern_func_73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001")]
    [SuppressGCTransition]
    private static partial void AddBls12381Scalar(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "mul_extern_func_73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001")]
    [SuppressGCTransition]
    private static partial void MulBls12381Scalar(nuint rd, nuint rs1, nuint rs2);

    [LibraryImport("__Internal", EntryPoint = "is_eq_extern_func_73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001")]
    [SuppressGCTransition]
    private static partial byte IsEqBls12381Scalar(nuint rs1, nuint rs2);
}
