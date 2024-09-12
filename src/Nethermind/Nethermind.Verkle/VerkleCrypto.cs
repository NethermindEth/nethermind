// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Verkle;

public static class VerkleCrypto
{
    private static readonly IntPtr _rust = RustVerkleLib.VerkleContextNew();

    public static void PedersenHash(byte[] address, byte[] treeIndexLe, byte[] outHas) =>
        RustVerkleLib.VerklePedersenhash(_rust, address, treeIndexLe, outHas);

    public static void PedersenHashFlat(byte[] addAndTreeIndexLe, byte[] outHas) =>
        RustVerkleLib.VerklePedersenhashFlat(_rust, addAndTreeIndexLe, outHas);

    public static void MSM(byte[] input, UIntPtr length, byte[] outHash) =>
        RustVerkleLib.VerkleMSM(_rust, input, length, outHash);

    public static void Prove(byte[] input, UIntPtr length, byte[] outHash) =>
        RustVerkleLib.VerkleProve(_rust, input, length, outHash);


    public static void Verify(byte[] input, UIntPtr length) => RustVerkleLib.VerkleVerify(_rust, input, length);

    public static void ProveUncompressed(byte[] input, UIntPtr length, byte[] outHash) =>
        RustVerkleLib.VerkleProveUncompressed(_rust, input, length, outHash);

    public static void VerifyUncompressed(byte[] input, UIntPtr length) =>
        RustVerkleLib.VerkleVerifyUncompressed(_rust, input, length);
}
