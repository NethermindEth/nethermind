// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable NETH003 // Build variant: only one of EthereumEcdsa.std.cs / EthereumEcdsa.zkevm.cs is compiled per build

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Crypto;

public sealed class EthereumEcdsa(ulong chainId) : IEthereumEcdsa
{
    public ulong ChainId => chainId;

    public Address? RecoverAddress(Signature signature, in ValueHash256 message)
    {
        Span<byte> publicKey = stackalloc byte[64];
        return RecoverAddressRaw(signature.Bytes, signature.RecoveryId, message.Bytes, publicKey)
            ? PublicKey.ComputeAddress(publicKey)
            : null;
    }

    public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) =>
        throw new NotSupportedException();

    public PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message)
    {
        Span<byte> publicKey = stackalloc byte[64];
        return RecoverAddressRaw(signature.Bytes, signature.RecoveryId, message.Bytes, publicKey)
            ? new(publicKey)
            : null;
    }

    public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => throw new NotSupportedException();

    /// <summary>Recovers the signer's SEC1 uncompressed public key, <c>0x04</c> prefix included.</summary>
    /// <remarks>The accelerator returns the 64-byte body only, so the prefix is written here to give callers the
    /// same shape the standard build produces.</remarks>
    public static bool RecoverPublicKeyRaw(
        ReadOnlySpan<byte> signature64,
        byte recoveryId,
        ReadOnlySpan<byte> message,
        Span<byte> publicKey65)
    {
        if (!RecoverAddressRaw(signature64, recoveryId, message, publicKey65[1..])) return false;

        publicKey65[0] = 0x04;
        return true;
    }

    public static bool RecoverAddressRaw(
        ReadOnlySpan<byte> signature,
        byte recoveryId,
        ReadOnlySpan<byte> message,
        Span<byte> publicKey
        ) => Accelerators.SecP256k1Recover(message, signature, recoveryId, publicKey) == Accelerators.Status.OK;
}
