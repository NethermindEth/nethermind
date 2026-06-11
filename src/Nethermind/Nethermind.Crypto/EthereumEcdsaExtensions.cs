// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto;

public static class EthereumEcdsaExtensions
{
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;
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
    public static bool Verify(this IEthereumEcdsa ecdsa, Address sender, Transaction tx)
    {
        Address? recovered = ecdsa.RecoverAddress(tx);
        return recovered?.Equals(sender) ?? false;
    }

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
        ValueHash256 hash = CalculateSignatureHash(ecdsa, tx, signature, useSignatureChainId);

        return ecdsa.RecoverAddress(signature, in hash);
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
            TxType.Legacy when useSignatureChainId => signature.ChainId.GetValueOrDefault(),
            TxType.Legacy => ecdsa.ChainId,
            _ => tx.ChainId
                ?? throw new InvalidDataException("Cannot recover signature hash from a typed transaction without a chain id."),
        };

        KeccakRlpWriter writer = new();
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
