// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public interface IEcdsa
    {
        Signature Sign(PrivateKey privateKey, Keccak message);
        Signature Sign(PrivateKey privateKey, in ValueKeccak message);
        PublicKey RecoverPublicKey(Signature signature, Keccak message);
        PublicKey RecoverPublicKey(Signature signature, in ValueKeccak message);
        CompressedPublicKey RecoverCompressedPublicKey(Signature signature, Keccak message);
        CompressedPublicKey RecoverCompressedPublicKey(Signature signature, in ValueKeccak message);
    }
}
