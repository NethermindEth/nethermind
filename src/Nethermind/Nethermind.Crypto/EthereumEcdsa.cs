// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

public class EthereumEcdsa(ulong chainId) : Ecdsa, IEthereumEcdsa
{
    public ulong ChainId => chainId;

    public Address? RecoverAddress(Signature signature, in ValueHash256 message)
    {
        Span<byte> publicKey = stackalloc byte[65];
        bool success = RecoverAddressRaw(
            signature.Bytes,
            signature.RecoveryId,
            message.Bytes,
            publicKey
            );

        return success ? PublicKey.ComputeAddress(publicKey[1..]) : null;
    }

    public static bool RecoverAddressRaw(
        ReadOnlySpan<byte> signature64,
        byte recoveryId,
        ReadOnlySpan<byte> message,
        Span<byte> publicKey65) =>
        SecP256k1.RecoverKeyFromCompact(publicKey65, message, signature64, recoveryId, false);
}
