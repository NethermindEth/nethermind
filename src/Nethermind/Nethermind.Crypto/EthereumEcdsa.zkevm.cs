// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

public sealed class EthereumEcdsa(ulong chainId) : IEthereumEcdsa
{
    public ulong ChainId => chainId;

    public Address? RecoverAddress(Signature signature, in ValueHash256 message)
    {
        Span<byte> output = stackalloc byte[32];
        byte success = ZiskBindings.Crypto.secp256k1_ecdsa_address_recover_c(
            signature.Bytes, signature.RecoveryId, message.Bytes, output);

        return success == 0 ? new(output[12..]) : null;
    }

    public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) =>
        throw new NotSupportedException();

    public PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message) => throw new NotSupportedException();

    public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => throw new NotSupportedException();

    public static bool RecoverAddressRaw(
        ReadOnlySpan<byte> signature,
        byte recoveryId,
        ReadOnlySpan<byte> message,
        Span<byte> output)
    {
        byte success = ZiskBindings.Crypto.secp256k1_ecdsa_address_recover_c(
            signature, recoveryId, message, output);

        return success == 0;
    }
}
