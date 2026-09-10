// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Org.BouncyCastle.Crypto;

namespace Nethermind.Crypto
{
    public interface IEciesCipher
    {
        /// <summary>Authenticates and decrypts an ECIES ciphertext.</summary>
        /// <returns>Success and the plaintext, which may be empty; otherwise <c>(false, null)</c> for an undersized ciphertext or unsupported public-key prefix.</returns>
        /// <exception cref="InvalidCipherTextException">The ciphertext MAC is invalid.</exception>
        (bool Success, byte[] PlainText) Decrypt(PrivateKey privateKey, byte[] cipherText, byte[]? macData = null);
        byte[] Encrypt(PublicKey recipientPublicKey, byte[] plainText, byte[]? macData = null);
    }
}
