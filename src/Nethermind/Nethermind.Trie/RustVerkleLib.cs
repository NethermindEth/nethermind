//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Trie;

[StructLayout(LayoutKind.Sequential)]
public class Proof
{
    public IntPtr ptr;
    public int len;
}

public static class RustVerkleLib {
    
    static RustVerkleLib()
    {
        LibResolver.Setup();
    }
    
    [DllImport("rust_verkle")]
    private static extern IntPtr verkle_trie_new();

    [DllImport("rust_verkle")]
    private static extern unsafe IntPtr verkle_trie_get(IntPtr verkleTrie, byte *  key);
    
    [DllImport("rust_verkle")]
    private static extern IntPtr get_root_hash(IntPtr verkleTrie);
    
    [DllImport("rust_verkle")]
    private static extern unsafe void verkle_trie_insert(IntPtr verkleTrie, byte * key, byte * value);
    
    [DllImport("rust_verkle")]
    private static extern unsafe IntPtr get_verkle_proof(IntPtr verkleTrie, byte * key);

    [DllImport("rust_verkle")]
    private static extern unsafe byte verify_verkle_proof(IntPtr verkleTrie, byte * verkleProof, int proofLen, byte * key, byte * value);

    [DllImport("rust_verkle")]
    private static extern unsafe void verkle_trie_insert_multiple(IntPtr verkleTrie, byte * keys, byte * values, int len);

    [DllImport("rust_verkle")]
    private static extern unsafe IntPtr get_verkle_proof_multiple(IntPtr verkleTrie, byte * keys, int len);

    [DllImport("rust_verkle")]
    private static extern unsafe byte verify_verkle_proof_multiple(IntPtr verkleTrie, byte * verkleProof, int proofLen, byte * keys, byte * values, int len);

    public static IntPtr VerkleTrieNew()
    {
        return verkle_trie_new();
    }
    
    public static unsafe void VerkleTrieInsert(IntPtr verkleTrie, Span<byte> key, Span<byte> value)
    {
        int valueLength = value.Length;
        if (valueLength != 32)
        {
            throw new InvalidOperationException("Value length must be less than 32");
        }

        fixed (byte* pKey = &MemoryMarshal.GetReference(key))
        {
            fixed (byte* pValue = &MemoryMarshal.GetReference(value))
            {
                verkle_trie_insert(verkleTrie, pKey, pValue);
            }
        }
    }
    
    public static unsafe void VerkleTrieInsert(IntPtr verkleTrie, byte[] key, byte[] value)
    {
        int valueLength = value.Length;
        if (valueLength != 32)
        {
            throw new InvalidOperationException("Value length must be less than 32");
        }

        fixed (byte* pKey = key)
        {
            fixed (byte* pValue = value)
            {
                verkle_trie_insert(verkleTrie, pKey, pValue);
            }
        }
    }

    public static unsafe byte[]? VerkleTrieGet(IntPtr verkleTrie, Span<byte> key)
    {
        fixed (byte* p = &MemoryMarshal.GetReference(key))
        {
            IntPtr value = verkle_trie_get(verkleTrie, p);
            if (value == IntPtr.Zero)
            {
                return null;
            }
            byte[] managedValue = new byte[32];
            Marshal.Copy(value, managedValue, 0, 32);
            return managedValue;
        }
        
    }
    
    public static unsafe byte[]? VerkleTrieGet(IntPtr verkleTrie, byte[] key)
    {
        fixed (byte* p = key)
        {
            IntPtr value = verkle_trie_get(verkleTrie, p);
            if (value == IntPtr.Zero)
            {
                return null;
            }
            byte[] managedValue = new byte[32];
            Marshal.Copy(value, managedValue, 0, 32);
            return managedValue;
        }
        
    }
    
    public static unsafe Span<byte> VerkleTrieGetSpan(IntPtr verkleTrie, Span<byte> key)
    {
        fixed (byte* p = &MemoryMarshal.GetReference(key))
        {
            IntPtr value = verkle_trie_get(verkleTrie, p);
            return value == IntPtr.Zero ? Span<byte>.Empty : new Span<byte>(value.ToPointer(), 32);
        }
    }
    
    public static unsafe Span<byte> VerkleTrieGetSpan(IntPtr verkleTrie, byte[] key)
    {
        fixed (byte* p = key)
        {
            IntPtr value = verkle_trie_get(verkleTrie, p);
            return value == IntPtr.Zero ? Span<byte>.Empty : new Span<byte>(value.ToPointer(), 32);
        }
    }
    
    public static byte[] VerkleTrieGetStateRoot(IntPtr verkleTrie)
    {
        IntPtr value = get_root_hash(verkleTrie);
        byte[] managedValue = new byte[32];
        Marshal.Copy(value, managedValue, 0, 32);
        return managedValue;
    }

    public static unsafe byte[] VerkleProofGet(IntPtr verkleTrie, byte[] key)
    {
        fixed (byte* p = key)
        {
            IntPtr proofBox =  get_verkle_proof(verkleTrie, p);
            Proof vp = (Proof)Marshal.PtrToStructure(proofBox, typeof(Proof));
            byte[] proofBytes = new byte[vp.len];
            Marshal.Copy(vp.ptr, proofBytes, 0, vp.len);
            return proofBytes;
        }
    }

    public static unsafe bool VerkleProofVerify(IntPtr verkleTrie, byte[] verkleProof, int proofLen, byte[] key, byte[] value)
    {
        fixed(byte* pProof = verkleProof)
        {
            fixed(byte* pKey = key)
            {
                fixed(byte* pValue = value)
                {
                    byte verification = verify_verkle_proof(verkleTrie, pProof, proofLen, pKey, pValue);
                    if (verification == 0)
                    {
                        return false;
                    }
                    return true;
                }
            }
        }
    }

    public static unsafe void VerkleTrieInsertMultiple(IntPtr verkleTrie, byte[,] keys, byte[,] vals, int len)
    {
        fixed (byte*  pKey = keys)
        {
            fixed (byte* pValue = vals)
            {
                verkle_trie_insert_multiple(verkleTrie, pKey, pValue, len);
            }
        }
    }

    public static unsafe byte[] VerkleProofGetMultiple(IntPtr verkleTrie, byte[,] keys, int len)
    {
        fixed(byte* pKey = keys)
        {
            IntPtr proofBox = get_verkle_proof_multiple(verkleTrie, pKey, len);
            Proof vp = (Proof)Marshal.PtrToStructure(proofBox, typeof(Proof));
            byte[] proofBytes = new byte[vp.len];
            Marshal.Copy(vp.ptr, proofBytes, 0, vp.len);
            return proofBytes;
        }
    }

    public static unsafe bool VerkleProofVerifyMultiple(IntPtr verkleTrie, byte[] verkleProof, int proofLen, byte[,] keys, byte[,] values, int len)
    {
        fixed(byte* pProof = verkleProof)
        {
            fixed(byte* pKey = keys)
            {
                fixed(byte* pValue = values)
                {
                    byte verification = verify_verkle_proof_multiple(verkleTrie, pProof, proofLen, pKey, pValue, len);
                    if (verification == 0)
                    {
                        return false;
                    }
                    return true;
                }
            }
        }
    }
}

