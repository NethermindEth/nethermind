// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
