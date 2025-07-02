// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public interface IEcdsa
    {
        Signature Sign(PrivateKey privateKey, Hash256 message)
            => Sign(privateKey, in message.ValueHash256);

        Signature Sign(PrivateKey privateKey, in ValueHash256 message);

        PublicKey? RecoverPublicKey(Signature signature, Hash256 message)
            => RecoverPublicKey(signature, in message.ValueHash256);

        PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message);

        CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, Hash256 message)
            => RecoverCompressedPublicKey(signature, in message.ValueHash256);

        CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message);
    }
}
