// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto;

public static class EthereumEcdsaExtensions
{
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

    /// <remarks>
    /// Cross-context cache of recovered senders keyed by transaction hash: a transaction recovered
    /// on mempool ingress becomes a lookup on block arrival. Legacy transactions are excluded —
    /// their signing hash depends on the ambient chain id, so the hash alone is not a safe global key.
    /// The key is sound only while <see cref="Transaction.Hash"/> matches the signed content; all
    /// ingress paths derive it from the raw bytes, and callers must not mutate a transaction's
    /// content or signature after the hash is set.
    /// </remarks>
    private const int SenderCacheCapacity = 1 << 15;
    private static readonly AssociativeCache<ValueHash256, Address> _senderCache = new(SenderCacheCapacity);

    /// <summary>Clears the process-wide sender cache. Intended for test isolation only.</summary>
    internal static void ClearSenderCache() => _senderCache.Clear();

    public static AuthorizationTuple Sign(this IEthereumEcdsa ecdsa, PrivateKey signer, ulong chainId, Address codeAddress, ulong nonce)
    {
        KeccakRlpWriter writer = new();
        AuthorizationTupleDecoder.EncodeSignaturePayload(ref writer, chainId, codeAddress, nonce);
        Signature sig = ecdsa.Sign(signer, writer.GetValueHash());
        return new AuthorizationTuple(chainId, codeAddress, nonce, sig);
    }

    public static void Sign(this IEthereumEcdsa ecdsa, PrivateKey privateKey, Transaction tx, bool isEip155Enabled = true)
    {
        if (tx.Type != TxType.Legacy)
        {
            tx.ChainId = ecdsa.ChainId;
        }

        ValueHash256 hash = CalculateSigningHash(tx, isEip155Enabled, ecdsa.ChainId);
        tx.Signature = ecdsa.Sign(privateKey, in hash);

        if (tx.Type == TxType.Legacy && isEip155Enabled)
        {
            tx.Signature.V = CalculateV(ecdsa.ChainId, tx.Signature.RecoveryId == 1);
        }
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="tx"></param>
    /// <returns></returns>
    public static bool Verify(this IEthereumEcdsa ecdsa, Address sender, Transaction tx) =>
        ecdsa.TryRecoverAddress(tx, out Address? recovered) && recovered.Equals(sender);

    /// <summary>
    /// Recovers the address that signed the transaction.
    /// </summary>
    /// <param name="ecdsa">The ECDSA implementation used for recovery.</param>
    /// <param name="tx">The transaction whose signature should be recovered.</param>
    /// <param name="useSignatureChainId">Whether to use the chain id encoded in a legacy EIP-155 signature.</param>
    /// <returns>The recovered address, or <see langword="null"/> when recovery fails.</returns>
    public static Address? RecoverAddress(this IEthereumEcdsa ecdsa, Transaction tx, bool useSignatureChainId = false)
    {
        Signature signature = tx.Signature
            ?? throw new InvalidDataException("Cannot recover sender address from a transaction without a signature.");

        return RecoverAddress(ecdsa, tx, signature, default, useSignatureChainId);
    }

    /// <summary>
    /// Recovers the address that signed the transaction, reading the signed payload out of the
    /// transaction's own encoding rather than encoding it again.
    /// </summary>
    /// <param name="ecdsa">The ECDSA implementation used for recovery.</param>
    /// <param name="tx">The transaction the encoding belongs to.</param>
    /// <param name="encoded">
    /// The transaction's canonical encoding with typed transactions left unwrapped, as
    /// <see cref="RlpBehaviors.SkipTypedWrapping"/> produces and an execution payload carries.
    /// </param>
    /// <param name="useSignatureChainId">Whether to use the chain id encoded in a legacy EIP-155 signature.</param>
    /// <returns>The recovered address, or <see langword="null"/> when recovery fails.</returns>
    /// <remarks>
    /// Shares the sender cache with <see cref="RecoverAddress(IEthereumEcdsa, Transaction, bool)"/>. An encoding whose
    /// trailing r and s differ from the transaction's signature is ignored and the transaction is encoded instead.
    /// </remarks>
    public static Address? RecoverAddress(this IEthereumEcdsa ecdsa, Transaction tx, ReadOnlySpan<byte> encoded, bool useSignatureChainId = false)
    {
        Signature signature = tx.Signature
            ?? throw new InvalidDataException("Cannot recover sender address from a transaction without a signature.");

        return RecoverAddress(ecdsa, tx, signature, encoded, useSignatureChainId);
    }

    /// <summary>
    /// Tries to recover the address that signed the transaction.
    /// </summary>
    /// <param name="ecdsa">The ECDSA implementation used for recovery.</param>
    /// <param name="tx">The transaction whose signature should be recovered.</param>
    /// <param name="address">The recovered address when recovery succeeds.</param>
    /// <param name="useSignatureChainId">Whether to use the chain id encoded in a legacy EIP-155 signature.</param>
    /// <returns><see langword="true"/> when the transaction has a recoverable sender address; otherwise <see langword="false"/>.</returns>
    public static bool TryRecoverAddress(this IEthereumEcdsa ecdsa, Transaction tx, [NotNullWhen(true)] out Address? address, bool useSignatureChainId = false)
    {
        if (tx.Signature is not { } signature)
        {
            address = null;
            return false;
        }

        address = RecoverAddress(ecdsa, tx, signature, default, useSignatureChainId);
        return address is not null;
    }

    private static Address? RecoverAddress(IEthereumEcdsa ecdsa, Transaction tx, Signature signature, ReadOnlySpan<byte> encoded, bool useSignatureChainId)
    {
        Hash256? txHash = tx.Type == TxType.Legacy ? null : tx.Hash;
        if (txHash is not null && _senderCache.TryGet(txHash.ValueHash256, out Address? cached))
        {
            return cached;
        }

        // The recovered sender is cached under this transaction's hash, so an encoding of another transaction must not be hashed.
        ValueHash256 hash = SignatureHash(ecdsa, tx, signature, encoded, useSignatureChainId, checkOwnership: true);
        Address? recovered = ecdsa.RecoverAddress(signature, in hash);

        if (txHash is not null && recovered is not null)
        {
            _senderCache.Set(txHash.ValueHash256, recovered);
        }

        return recovered;
    }

    /// <summary>
    /// Recovers the public key that signed the transaction.
    /// </summary>
    /// <param name="ecdsa">The ECDSA implementation used for recovery.</param>
    /// <param name="tx">The transaction whose signature should be recovered.</param>
    /// <param name="useSignatureChainId">Whether to use the chain id encoded in a legacy EIP-155 signature.</param>
    /// <returns>The recovered public key, or <see langword="null"/> when recovery fails.</returns>
    public static PublicKey? RecoverPublicKey(this IEthereumEcdsa ecdsa, Transaction tx, bool useSignatureChainId = false)
    {
        Signature signature = tx.Signature
            ?? throw new InvalidDataException("Cannot recover public key from a transaction without a signature.");
        ValueHash256 hash = CalculateSignatureHash(ecdsa, tx, signature, useSignatureChainId);

        return ecdsa.RecoverPublicKey(signature, in hash);
    }

    private static ValueHash256 CalculateSignatureHash(IEthereumEcdsa ecdsa, Transaction tx, Signature signature, bool useSignatureChainId)
    {
        (bool applyEip155, ulong chainId) = SigningParameters(ecdsa, tx, signature, useSignatureChainId);

        return CalculateSigningHash(tx, applyEip155, chainId);
    }

    private static ValueHash256 CalculateSigningHash(Transaction tx, bool isEip155Enabled, ulong chainId)
    {
        LengthList lengths = new(stackalloc int[LengthList.DefaultStackallocLength]);
        scoped KeccakRlpWriter writer = new();
        try
        {
            _txDecoder.PopulateTxLength(tx, lengths.BeginRootContainer(), RlpBehaviors.SkipTypedWrapping, true, isEip155Enabled, chainId);
            _txDecoder.EncodeTx(ref writer, tx, ref lengths, RlpBehaviors.SkipTypedWrapping, true, isEip155Enabled, chainId);
            return writer.GetValueHash();
        }
        finally
        {
            lengths.Dispose();
        }
    }

    /// <summary>Resolves whether the EIP-155 chain id triplet is part of the signed payload, and which chain id it carries.</summary>
    private static (bool ApplyEip155, ulong ChainId) SigningParameters(
        IEthereumEcdsa ecdsa, Transaction tx, Signature signature, bool useSignatureChainId)
    {
        useSignatureChainId &= signature.ChainId.HasValue;

        // Compared through the chain id the signature carries: 2 * chainId + 35 overflows a ulong for the largest chain ids.
        bool applyEip155 = useSignatureChainId || signature.ChainId == ecdsa.ChainId;
        ulong chainId = tx.Type switch
        {
            TxType.Legacy when useSignatureChainId => signature.ChainId
                ?? throw new InvalidDataException("Cannot recover signature hash from a legacy EIP-155 signature without a chain id."),
            TxType.Legacy => ecdsa.ChainId,
            _ => tx.ChainId
                ?? throw new InvalidDataException("Cannot recover signature hash from a typed transaction without a chain id."),
        };

        return (applyEip155, chainId);
    }

    /// <summary>
    /// Recovers the public key that signed the transaction, reading the signed payload out of the
    /// transaction's own encoding rather than encoding it again.
    /// </summary>
    /// <param name="ecdsa">The ECDSA implementation used for recovery.</param>
    /// <param name="tx">The transaction the encoding belongs to.</param>
    /// <param name="encoded">
    /// The transaction's canonical encoding with typed transactions left unwrapped, as
    /// <see cref="RlpBehaviors.SkipTypedWrapping"/> produces and an execution payload carries.
    /// </param>
    /// <param name="publicKey">Receives the 65-byte SEC1 uncompressed public key, <c>0x04</c> prefix included, when recovery succeeds.</param>
    /// <param name="useSignatureChainId">Whether to use the chain id encoded in a legacy EIP-155 signature.</param>
    /// <returns><see langword="true"/> when the transaction's signature yields a public key; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// A transaction's signed payload is its encoding without the trailing three signature items, so the hash
    /// can be taken over bytes that are already on the wire plus a shorter sequence header - and, for a legacy
    /// EIP-155 transaction, the chain id triplet that replaces the signature. That skips a full re-encode of the
    /// transaction, which is the bulk of recovery's cost inside a zkVM guest, and allocates nothing: the key goes
    /// to the caller's buffer.
    /// </remarks>
    public static bool TryRecoverPublicKey(
        this IEthereumEcdsa ecdsa, Transaction tx, ReadOnlySpan<byte> encoded, Span<byte> publicKey, bool useSignatureChainId = false)
    {
        if (tx.Signature is not { } signature) return false;

        ValueHash256 hash = SignatureHash(ecdsa, tx, signature, encoded, useSignatureChainId);

        return EthereumEcdsa.RecoverPublicKeyRaw(signature.Bytes, signature.RecoveryId, hash.Bytes, publicKey);
    }

    // A type whose signed bytes are not a prefix of its encoding, no encoding at all, or, when checked, the encoding
    // of another transaction falls back to encoding the transaction.
    private static ValueHash256 SignatureHash(
        IEthereumEcdsa ecdsa, Transaction tx, Signature signature, ReadOnlySpan<byte> encoded, bool useSignatureChainId, bool checkOwnership = false) =>
        TxDecoder.TryGetSignedPayload(encoded, tx.Type, out ReadOnlySpan<byte> signedPayload)
            && (!checkOwnership || EndsWithSignature(encoded, signedPayload, signature))
            ? SignedPayloadHash(ecdsa, tx, signature, signedPayload, useSignatureChainId)
            : CalculateSignatureHash(ecdsa, tx, signature, useSignatureChainId);

    // The r and s that follow the signed payload tie the encoding to the transaction it was decoded with.
    private static bool EndsWithSignature(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> signedPayload, Signature signature)
    {
        encoded.Overlaps(signedPayload, out int offset);
        RlpReader reader = new(encoded[(offset + signedPayload.Length)..]);
        reader.SkipItem();
        return reader.DecodeByteArraySpan().SequenceEqual(signature.RAsSpan.WithoutLeadingZeros())
            && reader.DecodeByteArraySpan().SequenceEqual(signature.SAsSpan.WithoutLeadingZeros());
    }

    /// <summary>Hashes a signed payload that is already encoded, behind the sequence header the signer used.</summary>
    /// <remarks>The message is assembled in one buffer and hashed in one pass, which is cheaper than feeding an
    /// incremental sponge write by write.</remarks>
    [SkipLocalsInit]
    private static ValueHash256 SignedPayloadHash(
        IEthereumEcdsa ecdsa, Transaction tx, Signature signature, scoped ReadOnlySpan<byte> signedPayload, bool useSignatureChainId)
    {
        (bool applyEip155, ulong chainId) = SigningParameters(ecdsa, tx, signature, useSignatureChainId);
        bool typed = tx.Type != TxType.Legacy;
        int eip155Length = !typed && applyEip155 && chainId != 0 ? Rlp.LengthOf(chainId) + 2 : 0;
        int contentLength = signedPayload.Length + eip155Length;
        int length = (typed ? 1 : 0) + Rlp.LengthOfSequence(contentLength);

        byte[]? rented = null;
        Span<byte> message = length <= StackMessageLimit
            ? stackalloc byte[StackMessageLimit]
            : rented = SafeArrayPool<byte>.Shared.Rent(length);

        RlpWriter writer = new(message);
        if (typed) WriteByte(ref writer, (byte)tx.Type);
        writer.StartSequence(contentLength);
        WriteRaw(ref writer, signedPayload);

        if (eip155Length > 0)
        {
            writer.Encode(chainId);
            // The two empty byte arrays standing in for r and s, as LegacyTxDecoder encodes them.
            WriteByte(ref writer, Rlp.EmptyByteArrayByte);
            WriteByte(ref writer, Rlp.EmptyByteArrayByte);
        }

        ValueHash256 hash = ValueKeccak.Compute(message[..length]);

        if (rented is not null) SafeArrayPool<byte>.Shared.Return(rented);

        return hash;
    }

    private const int StackMessageLimit = 1024;

    // The write primitives are explicit interface implementations, so they are reached through the constraint.
    private static void WriteByte<TWriter>(ref TWriter writer, byte value)
        where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.WriteByte(value);

    private static void WriteRaw<TWriter>(ref TWriter writer, scoped ReadOnlySpan<byte> bytes)
        where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.Write(bytes);

    /// <summary>The largest chain id for which both EIP-155 <c>v</c> values fit a ulong.</summary>
    public const ulong MaxEip155ChainId = (ulong.MaxValue - 36) / 2;

    /// <summary>EIP-155 <c>v</c> for <paramref name="chainId"/>; throws <see cref="OverflowException"/> where no such <c>v</c> fits a ulong.</summary>
    public static ulong CalculateV(ulong chainId, bool addParity = true)
        => TryCalculateV(chainId, addParity, out ulong v) ? v : throw new OverflowException($"No EIP-155 v fits a ulong for chain id {chainId}.");

    /// <summary>EIP-155 <c>v</c> for <paramref name="chainId"/>; false where no such <c>v</c> fits a ulong.</summary>
    public static bool TryCalculateV(ulong chainId, bool addParity, out ulong v)
    {
        ulong parity = addParity ? 1u : 0u;
        // Past the shared bound, only the parity-0 v (2^64 - 1) still fits.
        bool fits = chainId <= MaxEip155ChainId + (1 - parity);
        v = fits ? chainId * 2 + 35 + parity : 0;
        return fits;
    }

    public static Address? RecoverAddress(this IEthereumEcdsa ecdsa, AuthorizationTuple tuple)
    {
        KeccakRlpWriter writer = new();
        AuthorizationTupleDecoder.EncodeSignaturePayload(ref writer, tuple.ChainId, tuple.CodeAddress, tuple.Nonce);
        return ecdsa.RecoverAddress(tuple.AuthoritySignature, writer.GetValueHash());
    }
}
