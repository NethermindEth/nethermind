// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
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

    /// <summary>Sets the sender of each of <paramref name="transactions"/> from its signature, all in one call.</summary>
    /// <param name="transactions">The transactions, signed.</param>
    /// <param name="encodedTransactions">Their encodings, as <see cref="EthereumEcdsaExtensions.TryRecoverPublicKey"/> takes them.</param>
    /// <param name="ecdsa">The ECDSA implementation whose chain id the signing hashes use.</param>
    /// <param name="useSignatureChainId">Whether to use the chain id encoded in a legacy EIP-155 signature.</param>
    /// <returns>Whether every signature yields a key; when one does not, no sender is set.</returns>
    /// <remarks>
    /// Airbender only. libairbender recovers the keys together: the inverses of every r and of every recovered point's z
    /// are each one exponentiation for the whole block, where a recovery per call takes two of its own.
    /// </remarks>
    public static unsafe bool TryRecoverSendersInBatch(
        Transaction[] transactions, byte[][] encodedTransactions, IEthereumEcdsa ecdsa, bool useSignatureChainId)
    {
        int count = transactions.Length;
        byte[] messages = new byte[count * ValueHash256.MemorySize];
        byte[] signatures = new byte[count * 64];
        byte[] recoveryIds = new byte[count];
        byte[] keys = new byte[count * 64];
        byte[] recovered = new byte[count];

        for (int i = 0; i < count; i++)
        {
            Transaction tx = transactions[i];
            if (tx.Signature is not { } signature)
                return false;

            ValueHash256 hash = EthereumEcdsaExtensions.SignatureHash(ecdsa, tx, signature, encodedTransactions[i], useSignatureChainId);
            hash.Bytes.CopyTo(messages.AsSpan(i * ValueHash256.MemorySize));
            signature.Bytes.CopyTo(signatures.AsSpan(i * 64));
            recoveryIds[i] = signature.RecoveryId;
        }

        fixed (byte* m = messages, s = signatures, v = recoveryIds, k = keys, ok = recovered)
            airbender_secp256k1_ecrecover_batch(m, s, v, k, ok, (uint)count);

        if (recovered.AsSpan().Contains((byte)0))
            return false;

        for (int i = 0; i < count; i++)
            transactions[i].SenderAddress = PublicKey.ComputeAddress(keys.AsSpan(i * 64, 64));

        return true;
    }

    [DllImport("*")]
    private static extern unsafe void airbender_secp256k1_ecrecover_batch(
        byte* messages, byte* signatures, byte* recoveryIds, byte* keys, byte* recovered, uint count);
}
