// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        ) => _verified?.TryGetKey(signature, recoveryId, message, publicKey) == true
        || (ZiskFlag.IsActive
            ? Accelerators.ZiskSecP256k1Recover(message, signature, recoveryId, publicKey)
            : Accelerators.SecP256k1Recover(message, signature, recoveryId, publicKey)) == Accelerators.Status.OK;

    /// <summary>The recoveries the last <see cref="TryVerifyInBatch"/> checked, when it passed, which
    /// <see cref="RecoverAddressRaw"/> serves without recovering.</summary>
    private static VerifiedRecoveries? _verified;

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
        if (count == 0)
            return true;

        if (!TryPackSignatures(transactions, encodedTransactions, ecdsa, useSignatureChainId, count,
                out byte[] messages, out byte[] signatures, out byte[] recoveryIds))
            return false;

        byte[] keys = new byte[count * 64];
        byte[] recovered = new byte[count];
        fixed (byte* m = messages, s = signatures, v = recoveryIds, k = keys, ok = recovered)
            Accelerators.AirbenderSecP256k1RecoverBatch(m, s, v, k, ok, (uint)count);

        if (recovered.AsSpan().Contains((byte)0))
            return false;

        SetSenders(transactions, keys);
        return true;
    }

    /// <summary>Checks every transaction's signature against its signer's given key, and every other given recovery, in one
    /// call; when all hold, sets the senders and serves the other recoveries to <see cref="RecoverAddressRaw"/>.</summary>
    /// <param name="senderKeys">The signers' public keys, 64 bytes (x || y, big-endian) each, in transaction order.</param>
    /// <param name="senderYs">The big-endian y of each transaction signature's R, 32 bytes each.</param>
    /// <param name="others">Recoveries beyond the senders', such as of the ECRECOVER precompile, with their keys.</param>
    /// <param name="weights">16 random bytes per signature, the senders' first, drawn after every key and y is fixed.</param>
    /// <returns>Whether every signature yields its given key; when one does not, nothing is set.</returns>
    /// <remarks>
    /// Airbender only. A <see langword="false"/> says nothing about the signatures, as a key or a y may be what is wrong,
    /// so the caller then recovers the senders instead, and every other recovery is computed when it is needed. Either
    /// way, the recoveries an earlier call served are no longer served.
    /// libairbender's batch fails, as a recovery would, unless each recovery id is at most 3, r and s lie in [1, n - 1],
    /// R's x (r, plus n for ids 2 and 3), R's y and the key's coordinates are below p, R's y has the parity of the id,
    /// and R and the key are on the curve. A served key therefore never stands in for a recovery that fails.
    /// </remarks>
    public static unsafe bool TryVerifyInBatch(Transaction[] transactions, byte[][] encodedTransactions, IEthereumEcdsa ecdsa,
        bool useSignatureChainId, ReadOnlySpan<byte> senderKeys, ReadOnlySpan<byte> senderYs, in RecoveryHints others,
        ReadOnlySpan<byte> weights)
    {
        _verified = null;
        int senders = transactions.Length;
        int count = senders + others.Count;
        if (senderKeys.Length != senders * 64 || senderYs.Length != senders * 32 || weights.Length != count * 16
            || !others.HasWholeColumns
            || !TryPackSignatures(transactions, encodedTransactions, ecdsa, useSignatureChainId, count,
                out byte[] messages, out byte[] signatures, out byte[] recoveryIds))
            return false;

        byte[] keys = new byte[count * 64];
        byte[] ys = new byte[count * 32];
        senderKeys.CopyTo(keys);
        senderYs.CopyTo(ys);
        others.Messages.CopyTo(messages.AsSpan(senders * ValueHash256.MemorySize));
        others.Signatures.CopyTo(signatures.AsSpan(senders * 64));
        others.RecoveryIds.CopyTo(recoveryIds.AsSpan(senders));
        others.Keys.CopyTo(keys.AsSpan(senders * 64));
        others.Ys.CopyTo(ys.AsSpan(senders * 32));

        bool verified;
        fixed (byte* m = messages, s = signatures, v = recoveryIds, k = keys, y = ys, w = weights)
            verified = Accelerators.AirbenderSecP256k1VerifyBatch(m, s, v, k, y, w, (uint)count);

        if (!verified)
            return false;

        SetSenders(transactions, keys);
        _verified = new VerifiedRecoveries(messages[(senders * ValueHash256.MemorySize)..], signatures[(senders * 64)..],
            recoveryIds[senders..], keys[(senders * 64)..]);
        return true;
    }

    /// <summary>Recoveries given with their keys, column by column: 32-byte messages, 64-byte signatures (r || s),
    /// one-byte recovery ids, 64-byte keys (x || y) and the 32-byte y of each signature's R, all big-endian.</summary>
    public readonly ref struct RecoveryHints(
        ReadOnlySpan<byte> messages, ReadOnlySpan<byte> signatures, ReadOnlySpan<byte> recoveryIds, ReadOnlySpan<byte> keys,
        ReadOnlySpan<byte> ys)
    {
        public int Count => RecoveryIds.Length;
        public ReadOnlySpan<byte> Messages { get; } = messages;
        public ReadOnlySpan<byte> Signatures { get; } = signatures;
        public ReadOnlySpan<byte> RecoveryIds { get; } = recoveryIds;
        public ReadOnlySpan<byte> Keys { get; } = keys;
        public ReadOnlySpan<byte> Ys { get; } = ys;

        /// <summary>Whether every column holds <see cref="Count"/> entries.</summary>
        public bool HasWholeColumns =>
            Messages.Length == Count * 32 && Signatures.Length == Count * 64 && Keys.Length == Count * 64 && Ys.Length == Count * 32;
    }

    /// <param name="capacity">The signatures to make room for, the transactions' first.</param>
    private static bool TryPackSignatures(Transaction[] transactions, byte[][] encodedTransactions, IEthereumEcdsa ecdsa,
        bool useSignatureChainId, int capacity, out byte[] messages, out byte[] signatures, out byte[] recoveryIds)
    {
        int count = transactions.Length;
        messages = new byte[capacity * ValueHash256.MemorySize];
        signatures = new byte[capacity * 64];
        recoveryIds = new byte[capacity];

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

        return true;
    }

    private static void SetSenders(Transaction[] transactions, ReadOnlySpan<byte> keys)
    {
        for (int i = 0; i < transactions.Length; i++)
            transactions[i].SenderAddress = PublicKey.ComputeAddress(keys.Slice(i * 64, 64));
    }
}
