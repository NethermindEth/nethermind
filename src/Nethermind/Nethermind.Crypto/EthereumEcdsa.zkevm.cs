// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Crypto;

#pragma warning disable NETH003 // Build variant: only one of EthereumEcdsa.std.cs / EthereumEcdsa.zkevm.cs is compiled per build
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

    public static bool RecoverAddressRaw(
        ReadOnlySpan<byte> signature,
        byte recoveryId,
        ReadOnlySpan<byte> message,
        Span<byte> publicKey
        ) => IsValidSignature(signature)
            && Accelerators.SecP256k1Recover(message, signature, recoveryId, publicKey) == Accelerators.Status.OK;

    /// <summary>
    /// Validates that the signature scalars <c>r</c> and <c>s</c> lie in <c>[1, n-1]</c>.
    /// </summary>
    /// <remarks>
    /// The zkVM secp256k1 accelerator computes a modular inverse over <c>n</c> during recovery, which is
    /// undefined when <c>r</c> or <c>s</c> is zero or not less than the group order; passing such values aborts
    /// the guest. Rejecting them here mirrors the graceful failure of libsecp256k1 used by the standard build.
    /// </remarks>
    private static bool IsValidSignature(ReadOnlySpan<byte> signature)
    {
        UInt256 r = new(signature[..32], isBigEndian: true);
        UInt256 s = new(signature.Slice(32, 32), isBigEndian: true);
        return !r.IsZero && r < SecP256k1Curve.N && !s.IsZero && s < SecP256k1Curve.N;
    }
}
