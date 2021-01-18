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

using System;

namespace Nethermind.KeyStore.Config
{
    /// <summary>
    /// https://medium.com/@julien.maffre/what-is-an-ethereum-keystore-file-86c8c5917b97
    /// https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition
    /// </summary>
    public class KeyStoreConfig : IKeyStoreConfig
    {
        public string KeyStoreDirectory { get; set; } = "keystore";
        
        public string KeyStoreEncoding { get; set; } = "UTF-8";
        
        public string Kdf { get; set; } = "scrypt";
        
        public string Cipher { get; set; } = "aes-128-ctr";
        
        public int KdfparamsDklen { get; set; } = 32;
        
        public int KdfparamsN { get; set; } = 262144;
        
        public int KdfparamsP { get; set; } = 1;
        
        public int KdfparamsR { get; set; } = 8;
        
        public int KdfparamsSaltLen { get; set; } = 32;
        
        public int SymmetricEncrypterBlockSize { get; set; } = 128;
        
        public int SymmetricEncrypterKeySize { get; set; } = 128;
        
        public int IVSize { get; set; } = 16;
        
        public string TestNodeKey { get; set; }
        
        public string BlockAuthorAccount { get; set; }
        
        public string EnodeAccount { get; set; }
        
        public string EnodeKeyFile { get; set; }

        public string[] Passwords { get; set; } = Array.Empty<string>();
        
        public string[] PasswordFiles { get; set; } = Array.Empty<string>();
        
        public string[] UnlockAccounts { get; set; } = Array.Empty<string>();
    }
}
