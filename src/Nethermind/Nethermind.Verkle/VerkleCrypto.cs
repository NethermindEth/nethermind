// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Verkle;

public static class VerkleCrypto
{
    private static readonly IntPtr ContextPtr = RustVerkleLib.VerkleContextNew();

    public static void PedersenHash(byte[] address, byte[] treeIndexLe, byte[] outHas)
    {
        RustVerkleLib.PedersenHash(ContextPtr, address, treeIndexLe, outHas);
    }

    public static void PedersenHashFlat(Span<byte> addAndTreeIndexLe, byte[] outHas)
    {
        unsafe
        {
            fixed (byte* p = addAndTreeIndexLe)
            {
                RustVerkleLib.PedersenHashFlat(ContextPtr, p, outHas);
            }
        }
    }

    public static void MultiScalarMul(byte[] input, UIntPtr length, byte[] outHash)
    {
        RustVerkleLib.MultiScalarMul(ContextPtr, input, length, outHash);
    }

    public static void Prove(byte[] input, UIntPtr length, byte[] outHash)
    {
        RustVerkleLib.Prove(ContextPtr, input, length, outHash);
    }


    public static void Verify(byte[] input, UIntPtr length)
    {
        RustVerkleLib.Verify(ContextPtr, input, length);
    }

    public static void ProveUncompressed(byte[] input, UIntPtr length, byte[] outHash)
    {
        RustVerkleLib.ProveUncompressed(ContextPtr, input, length, outHash);
    }

    public static void VerifyUncompressed(byte[] input, UIntPtr length)
    {
        RustVerkleLib.VerifyUncompressed(ContextPtr, input, length);
    }

    public static void GetLeafDeltaBothValue(byte subIndex, byte[] oldValue, byte[] newValue, byte[] output)
    {
        RustVerkleLib.GetLeafDeltaBothValue(ContextPtr, subIndex, oldValue, newValue, output);
    }

    public static void GetLeafDeltaNewValue(byte subIndex, byte[] newValue, byte[] output)
    {
        RustVerkleLib.GetLeafDeltaNewValue(ContextPtr, subIndex, newValue, output);
    }
}
