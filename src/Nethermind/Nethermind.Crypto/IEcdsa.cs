// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public interface IEcdsa
    {
        Signature Sign(PrivateKey privateKey, Commitment message);
        PublicKey RecoverPublicKey(Signature signature, Commitment message);
        CompressedPublicKey RecoverCompressedPublicKey(Signature signature, Commitment message);
    }
}
