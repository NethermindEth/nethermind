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

namespace Nethermind.Trie
{
    public static class RustVerkleLib {
        
        static RustVerkleLib()
        {
            LibResolver.Setup();
        }
        
        [DllImport("rust_verkle")]
        public static extern IntPtr verkle_trie_new();

        [DllImport("rust_verkle")]
        public static extern byte[] verkle_trie_get(IntPtr verkleTrie, byte[] keys);
        
        [DllImport("rust_verkle")]
        public static extern IntPtr verkle_trie_insert(IntPtr verkleTrie, byte[] keys, byte[] value);

    }

}
