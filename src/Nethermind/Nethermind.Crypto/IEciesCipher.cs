// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public interface IEciesCipher
    {
        EciesDecryptionResult Decrypt(PrivateKey privateKey, byte[] cipherText, byte[]? macData = null);
        byte[] Encrypt(PublicKey recipientPublicKey, byte[] plainText, byte[]? macData = null);
    }

    public readonly struct EciesDecryptionResult
    {
        private EciesDecryptionResult(bool success, byte[]? plainText)
        {
            Success = success;
            PlainText = plainText;
        }

        [MemberNotNullWhen(true, nameof(PlainText))]
        public bool Success { get; }

        public byte[]? PlainText { get; }

        public static EciesDecryptionResult Failed => new(false, null);

        public static EciesDecryptionResult Succeeded(byte[] plainText)
            => new(true, plainText ?? throw new ArgumentNullException(nameof(plainText)));

        public void Deconstruct(out bool success, out byte[]? plainText)
        {
            success = Success;
            plainText = PlainText;
        }
    }
}
