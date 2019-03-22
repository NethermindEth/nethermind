/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

namespace Nethermind.KeyStore.Config
{
    public class KeyStoreConfig : IKeyStoreConfig
    {
        public string KeyStoreDirectory { get; set; } = "keystore";
        public string KeyStoreEncoding { get; set; } = "UTF-8";
        public string Kdf { get; set; } = "scrypt";
        public string Cipher { get; set; } = "aes-128-cbc";
        public int KdfparamsDklen { get; set; } = 32;
        public int KdfparamsN { get; set; } = 262144;
        public int KdfparamsP { get; set; } = 1;
        public int KdfparamsR { get; set; } = 8;
        
        public int KdfparamsSaltLen { get; set; } = 32;
        public int SymmetricEncrypterBlockSize { get; set; } = 128;
        public int SymmetricEncrypterKeySize { get; set; } = 128;
        public int IVSize { get; set; } = 16;
        public string TestNodeKey { get; set; }
    }
}