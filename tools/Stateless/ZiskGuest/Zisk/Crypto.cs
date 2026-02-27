// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable CA1401 // P/Invokes should not be visible
#pragma warning disable SYSLIB1054 // Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time

using System.Runtime.InteropServices;

namespace Nethermind.Stateless.ZiskGuest.Zisk;

public static unsafe class Crypto
{
    [DllImport("__Internal")]
    public static extern byte secp256k1_ecdsa_address_recover_c(
        byte* sig,
        byte* recid,
        byte* msg,
        byte* output);

    [DllImport("__Internal")]
    public static extern byte secp256k1_ecdsa_verify_and_address_recover_c(
        byte* sig,
        byte* msg,
        byte* pk,
        byte* output);
}
