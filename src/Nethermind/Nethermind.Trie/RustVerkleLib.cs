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

namespace Nethermind.Trie
{
    public static class RustVerkleLib {
        
        static RustVerkleLib()
        {
            LibResolver.Setup();
        }
        
        [DllImport("rust_verkle")]
        private static extern IntPtr verkle_trie_new();

        [DllImport("rust_verkle")]
        private static extern IntPtr verkle_trie_get(IntPtr verkleTrie, byte[] keys);
        
        [DllImport("rust_verkle")]
        private static extern IntPtr verkle_trie_insert(IntPtr verkleTrie, byte[] keys, byte[] value);
        
        public static IntPtr VerkleTrieNew()
        {
            return verkle_trie_new();
        }
        
        public static void VerkleTrieInsert(IntPtr verkleTrie, byte[] keys, byte[] value)
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

            verkle_trie_insert(verkleTrie, keys, newValue);
        }

        public static byte[]? VerkleTrieGet(IntPtr verkleTrie, byte[] keys)
        {
            IntPtr value = verkle_trie_get(verkleTrie, keys);
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

    }

}
