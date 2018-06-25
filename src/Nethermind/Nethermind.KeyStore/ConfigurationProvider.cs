///*
// * Copyright (c) 2018 Demerzel Solutions Limited
// * This file is part of the Nethermind library.
// *
// * The Nethermind library is free software: you can redistribute it and/or modify
// * it under the terms of the GNU Lesser General Public License as published by
// * the Free Software Foundation, either version 3 of the License, or
// * (at your option) any later version.
// *
// * The Nethermind library is distributed in the hope that it will be useful,
// * but WITHOUT ANY WARRANTY; without even the implied warranty of
// * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// * GNU Lesser General Public License for more details.
// *
// * You should have received a copy of the GNU Lesser General Public License
// * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// */

//using System.IO;
//using System.Text;

//namespace Nethermind.KeyStore
//{
//    public class ConfigurationProvider : IConfigurationProvider
//    {
//        public string KeyStoreDirectory => Path.GetDirectoryName(Path.Combine(Path.GetTempPath(), "KeyStore"));
//        public Encoding KeyStoreEncoding => Encoding.UTF8;
//        public string Kdf => "scrypt";
//        public string Cipher => "aes-128-cbc";
//        public int KdfparamsDklen => 32;
//        public int KdfparamsN => 262144;
//        public int KdfparamsP => 1;
//        public int KdfparamsR => 8;
//        public int KdfparamsSaltLen => 32;
//        public int SymmetricEncrypterBlockSize => 128;
//        public int SymmetricEncrypterKeySize => 128;
//        public int IVSize => 16;
//    }
//}