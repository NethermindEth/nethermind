// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public interface IEciesCipher
    {
        (bool Success, byte[] PlainText) Decrypt(PrivateKey privateKey, byte[] cipherText, byte[]? macData = null);
        byte[] Encrypt(PublicKey recipientPublicKey, byte[] plainText, byte[]? macData = null);
    }
}
