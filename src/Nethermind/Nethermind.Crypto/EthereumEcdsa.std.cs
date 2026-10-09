// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

#pragma warning disable NETH003 // Build variant: only one of EthereumEcdsa.std.cs / EthereumEcdsa.zkevm.cs is compiled per build
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

    /// <summary>Recovers the signer's SEC1 uncompressed public key, <c>0x04</c> prefix included.</summary>
    /// <remarks>Named apart from <see cref="RecoverAddressRaw"/> because the zkVM build recovers into a 64-byte
    /// buffer and has to add the prefix itself; callers that want one shape across both builds use this.</remarks>
    public static bool RecoverPublicKeyRaw(
        ReadOnlySpan<byte> signature64,
        byte recoveryId,
        ReadOnlySpan<byte> message,
        Span<byte> publicKey65) =>
        RecoverAddressRaw(signature64, recoveryId, message, publicKey65);

    public static bool RecoverAddressRaw(
        ReadOnlySpan<byte> signature64,
        byte recoveryId,
        ReadOnlySpan<byte> message,
        Span<byte> publicKey65) =>
        SecP256k1.RecoverKeyFromCompact(publicKey65, message, signature64, recoveryId, false);
}
