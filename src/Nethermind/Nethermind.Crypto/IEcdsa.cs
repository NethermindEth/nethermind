// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public interface IEcdsa
    {
        Signature Sign(PrivateKey privateKey, Keccak message);
        PublicKey RecoverPublicKey(Signature signature, Keccak message);
        CompressedPublicKey RecoverCompressedPublicKey(Signature signature, Keccak message);
    }
}
