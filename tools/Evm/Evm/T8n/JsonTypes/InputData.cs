// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Evm.T8n.Errors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Evm.T8n.JsonTypes;

public class InputData
{
    public Dictionary<Address, AccountState>? Alloc { get; set; }
    public EnvJson? Env { get; set; }
    public TransactionForRpc[]? Txs { get; set; }
    public TransactionMetaData[]? TransactionMetaDataList { get; set; }
    public string? TxRlp { get; set; }

    public Transaction[] GetTransactions(IRlpDecoder<Transaction> decoder, ulong chainId)
    {
        if (TxRlp is not null)
        {
            RlpReader ctx = new(Bytes.FromHexString(TxRlp));
            return decoder.DecodeNonNullArray(ref ctx);
        }

        List<Transaction> transactions = [];
        if (Txs is not null && TransactionMetaDataList is not null)
        {
            EthereumEcdsa ecdsa = new(chainId);

            for (int i = 0; i < Txs.Length; i++)
            {
                Transaction transaction = (Transaction)Txs[i].ToTransaction();

                if (transaction.Type.SupportsFrames())
                {
                    FrameTransactionForRpc frameTransactionForRpc = (FrameTransactionForRpc)Txs[i];
                    transaction.SenderAddress = frameTransactionForRpc.From;
                    transaction.ChainId ??= chainId;
                    SignFrameTransaction(transaction, TransactionMetaDataList[i]);
                    transaction.Hash = transaction.CalculateHash();

                    transactions.Add(transaction);
                    continue;
                }

                transaction.SenderAddress = null; // t8n does not accept SenderAddress from input, so need to reset senderAddress

                SignTransaction(transaction, TransactionMetaDataList[i], (LegacyTransactionForRpc)Txs[i]);

                transaction.ChainId ??= chainId;
                transaction.SenderAddress ??= ecdsa.RecoverAddress(transaction);
                transaction.Hash = transaction.CalculateHash();

                transactions.Add(transaction);
            }
        }

        return transactions.ToArray();
    }

    private static void SignTransaction(Transaction transaction, TransactionMetaData transactionMetaData, LegacyTransactionForRpc txLegacy)
    {
        if (transactionMetaData.SecretKey is not null)
        {
            PrivateKey privateKey = new(transactionMetaData.SecretKey);
            transaction.SenderAddress = privateKey.Address;

            EthereumEcdsa ecdsa = new(transaction.ChainId ?? TestBlockchainIds.ChainId);

            ecdsa.Sign(privateKey, transaction, transactionMetaData.Protected ?? true);
        }
        else if (txLegacy.R.HasValue && txLegacy.S.HasValue && txLegacy.V.HasValue)
        {
            transaction.Signature = new Signature(txLegacy.R.Value, txLegacy.S.Value, txLegacy.V.Value.ToUInt64(null));
        }
    }

    private static void SignFrameTransaction(Transaction transaction, TransactionMetaData transactionMetaData)
    {
        using PrivateKey? privateKey = transactionMetaData.SecretKey is null ? null : new PrivateKey(transactionMetaData.SecretKey);

        if (privateKey is not null)
        {
            transaction.SenderAddress ??= privateKey.Address;
        }

        if (transaction.SenderAddress is null)
        {
            throw new T8nException("frame transaction requires a sender or a secretKey", T8nErrorCodes.ErrorJson);
        }

        TxFrameClaim[]? claims = transaction.FrameClaims;
        if (privateKey is null || claims is null)
        {
            return;
        }

        // The signature hash covers the claims but not their witnesses, so filling one leaves the others valid.
        byte[][] witnesses = transaction.ClaimWitnesses ?? new byte[claims.Length][];
        if (witnesses.Length != claims.Length)
        {
            throw new T8nException("frame transaction must carry one witness per claim", T8nErrorCodes.ErrorJson);
        }

        Ecdsa ecdsa = new();
        ValueHash256? sigHash = null;
        for (int i = 0; i < claims.Length; i++)
        {
            TxFrameClaim claim = claims[i];
            if (witnesses[i] is { Length: > 0 } || claim.ResolvedWitnessLength == 0)
            {
                witnesses[i] ??= [];
                continue;
            }

            if (claim.Verifier != Eip8141Constants.Secp256k1VerifierAddress)
            {
                throw new T8nException($"cannot fill frame claim {i}: only secp256k1 claims are fillable", T8nErrorCodes.ErrorJson);
            }

            if (claim.ResolveKey(transaction.SenderAddress) != privateKey.Address.ToHash())
            {
                throw new T8nException(claim.Key.IsEmpty
                    ? "frame transaction sender does not match secretKey"
                    : "frame claim key does not match secretKey", T8nErrorCodes.ErrorJson);
            }

            ValueHash256 msg = claim.ResolveMsg(claim.SignsCanonicalHash ? sigHash ??= FrameTxSigHash.ComputeValue(transaction) : default);
            Signature signature = ecdsa.Sign(privateKey, in msg);
            byte[] vrs = new byte[Eip8141Constants.Secp256k1WitnessLength];
            vrs[0] = signature.RecoveryId;
            signature.Bytes.CopyTo(vrs.AsSpan(1));
            witnesses[i] = vrs;
        }

        transaction.ClaimWitnesses = witnesses;
    }
}
