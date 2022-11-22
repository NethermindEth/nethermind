// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.KeyStore
{
    public interface ISymmetricEncrypter
    {
        byte[] Encrypt(byte[] content, byte[] key, byte[] iv, string cipherType);
        byte[] Decrypt(byte[] cipher, byte[] key, byte[] iv, string cipherType);
    }
}
