// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto;

public static class EthereumEcdsaExtensions
{
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

    /// <remarks>
    /// Cross-context cache of recovered senders: a transaction recovered on mempool ingress becomes a
    /// lookup on block arrival. The key is derived from exactly what recovery reads, the signing hash
    /// and the signature (<see cref="CalculateSenderCacheKey"/>), so an entry is correct for every transaction
    /// that maps to it. Legacy transactions are not cached.
    /// </remarks>
    private const int SenderCacheCapacity = 1 << 15;
    private static readonly AssociativeCache<ValueHash256, Address> _senderCache = new(SenderCacheCapacity);

    /// <remarks>Every recovery computes a signing hash, a cache hit included, so the hasher is reused rather than allocated.</remarks>
    [ThreadStatic]
    private static KeccakHash? _signingHasher;

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

        KeccakRlpWriter writer = new();
        _txDecoder.EncodeTx(ref writer, tx, RlpBehaviors.SkipTypedWrapping, true, isEip155Enabled, ecdsa.ChainId);
        ValueHash256 hash = writer.GetValueHash();
        tx.Signature = ecdsa.Sign(privateKey, in hash);

        if (tx.Type == TxType.Legacy && isEip155Enabled)
        {
            tx.Signature.V = tx.Signature.V + 8 + 2 * ecdsa.ChainId;
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

        return RecoverAddress(ecdsa, tx, signature, useSignatureChainId);
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

        address = RecoverAddress(ecdsa, tx, signature, useSignatureChainId);
        return address is not null;
    }

    private static Address? RecoverAddress(IEthereumEcdsa ecdsa, Transaction tx, Signature signature, bool useSignatureChainId)
    {
        ValueHash256 hash = CalculateSignatureHash(ecdsa, tx, signature, useSignatureChainId);
        bool cacheable = tx.Type != TxType.Legacy;
        ValueHash256 key = default;
        if (cacheable)
        {
            key = CalculateSenderCacheKey(in hash, signature);
            if (_senderCache.TryGet(key, out Address? cached))
            {
                return cached;
            }
        }

        Address? recovered = ecdsa.RecoverAddress(signature, in hash);

        if (cacheable && recovered is not null)
        {
            _senderCache.Set(key, recovered);
        }

        return recovered;
    }

    /// <summary>The sender cache key: keccak(signing hash || r || s || recovery id).</summary>
    [SkipLocalsInit]
    private static ValueHash256 CalculateSenderCacheKey(in ValueHash256 signingHash, Signature signature)
    {
        Span<byte> input = stackalloc byte[ValueHash256.MemorySize + Signature.Size];
        signingHash.Bytes.CopyTo(input);
        signature.WriteBytesWithRecoveryTo(input[ValueHash256.MemorySize..]);
        return ValueKeccak.Compute(input);
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
        useSignatureChainId &= signature.ChainId.HasValue;

        // feels like it is the same check twice
        bool applyEip155 = useSignatureChainId
                           || signature.V == CalculateV(ecdsa.ChainId, false)
                           || signature.V == CalculateV(ecdsa.ChainId, true);
        ulong chainId = tx.Type switch
        {
            TxType.Legacy when useSignatureChainId => signature.ChainId
                ?? throw new InvalidDataException("Cannot recover signature hash from a legacy EIP-155 signature without a chain id."),
            TxType.Legacy => ecdsa.ChainId,
            _ => tx.ChainId
                ?? throw new InvalidDataException("Cannot recover signature hash from a typed transaction without a chain id."),
        };

        KeccakHash hasher = _signingHasher ??= KeccakHash.Create();
        // Reset first, so an exception that interrupted a previous encoding cannot leave this thread's hasher partly fed.
        hasher.Reset();
        KeccakRlpWriter writer = new(hasher);
        _txDecoder.EncodeTx(ref writer, tx, RlpBehaviors.SkipTypedWrapping, true, applyEip155, chainId);

        return writer.GetValueHash();
    }

    public static ulong CalculateV(ulong chainId, bool addParity = true) => chainId * 2 + 35ul + (addParity ? 1u : 0u);

    public static Address? RecoverAddress(this IEthereumEcdsa ecdsa, AuthorizationTuple tuple)
    {
        KeccakRlpWriter writer = new();
        AuthorizationTupleDecoder.EncodeSignaturePayload(ref writer, tuple.ChainId, tuple.CodeAddress, tuple.Nonce);
        return ecdsa.RecoverAddress(tuple.AuthoritySignature, writer.GetValueHash());
    }
}
