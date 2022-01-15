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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace Nethermind.Trie
{
    [StructLayout(LayoutKind.Sequential)]
    public class Proof{
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
        private static extern IntPtr verkle_trie_get(IntPtr verkleTrie, byte[] key);
        
        [DllImport("rust_verkle")]
        private static extern void verkle_trie_insert(IntPtr verkleTrie, byte[] key, byte[] value);
        
        [DllImport("rust_verkle")]
        private static extern IntPtr get_verkle_proof(IntPtr verkleTrie, byte[] key);

        [DllImport("rust_verkle")]
        private static extern bool verify_verkle_proof(IntPtr verkleTrie, byte[] verkleProof, int proof_len, byte[] key, byte[] value);

        [DllImport("rust_verkle")]
        private static extern void verkle_trie_insert_multiple(IntPtr verkleTrie, byte[,] keys, byte[,] vals, int len);

        [DllImport("rust_verkle")]
        private static extern IntPtr get_verkle_proof_multiple(IntPtr verkleTrie, byte[,] keys, int len);

        [DllImport("rust_verkle")]
        private static extern bool verify_verkle_proof_multiple(IntPtr verkleTrie, byte[] verkleProof, int proof_len, byte[,] key, byte[,] value, int len);

        public static IntPtr VerkleTrieNew()
        {
            return verkle_trie_new();
        }
        
        public static void VerkleTrieInsert(IntPtr verkleTrie, byte[] key, byte[] value)
        {
            byte[] newValue;
            int valueLength = value.Length;
            if (valueLength > 32)
            {
                throw new InvalidOperationException("Value length must be less than 32");
            } else if (valueLength < 32)
            {
                newValue = new byte[32];
                int lengthDiff = 32 - valueLength;
                byte[] addressPadding = new byte[12] ;
                Buffer.BlockCopy(value, 0, newValue, lengthDiff, valueLength);
            }
            else
            {
                newValue = value;
            }

            verkle_trie_insert(verkleTrie, key, newValue);
        }

        public static byte[]? VerkleTrieGet(IntPtr verkleTrie, byte[] key)
        {
            IntPtr value = verkle_trie_get(verkleTrie, key);
            if (value == IntPtr.Zero)
            {
                return null;
            }
            // Span<byte> bytes = new Span<byte>(x.ToPointer(), 32);
            // Console.WriteLine(bytes.ToArray());
            // return bytes.ToArray();
            byte[] managedValue = new byte[32];
            Marshal.Copy(value, managedValue, 0, 32);
            return managedValue;
        }

        public static byte[] VerkleProofGet(IntPtr verkleTrie, byte[] key){
            IntPtr proof_box =  get_verkle_proof(verkleTrie, key);
            Proof vp = (Proof)Marshal.PtrToStructure(proof_box, typeof(Proof));
            byte[] proof_bytes = new byte[vp.len];
            Marshal.Copy(vp.ptr, proof_bytes, 0, vp.len);
            return proof_bytes;
        }

        public static bool VerkleProofVerify(IntPtr verkleTrie, byte[] verkleProof, int proof_len, byte[] key, byte[] value){
            return verify_verkle_proof(verkleTrie, verkleProof, proof_len, key, value);
        }

        public static void VerkleTrieInsertMultiple(IntPtr verkleTrie, byte[,] keys, byte[,] vals, int len){
            verkle_trie_insert_multiple(verkleTrie, keys, vals, len);
        }

        public static byte[] VerkleProofGetMultiple(IntPtr verkleTrie, byte[,] keys, int len){
            IntPtr proof_box =  get_verkle_proof_multiple(verkleTrie, keys, len);
            Proof vp = (Proof)Marshal.PtrToStructure(proof_box, typeof(Proof));
            byte[] proof_bytes = new byte[vp.len];
            Marshal.Copy(vp.ptr, proof_bytes, 0, vp.len);
            return proof_bytes;
        }

        public static bool VerkleProofVerifyMultiple(IntPtr verkleTrie, byte[] verkleProof, int proof_len, byte[,] keys, byte[,] vals, int len){
            return verify_verkle_proof_multiple(verkleTrie, verkleProof, proof_len, keys, vals, len);
        }

    }

}
