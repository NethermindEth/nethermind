// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Evm.T8n.Errors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
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
            return decoder.DecodeArray(ref ctx);
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
                    // There is no RLP round-trip on this path, so the gas limit the decoder would have
                    // derived from the frames has to be derived here too. Otherwise the JSON `gas` field
                    // (or its absence) decides the limit and t8n executes the transaction differently
                    // from any client that decoded the same bytes.
                    transaction.GasLimit = FrameTxValidation.TotalGasLimit(transaction.Frames);
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
        PrivateKey? privateKey = transactionMetaData.SecretKey is null ? null : new PrivateKey(transactionMetaData.SecretKey);

        if (privateKey is not null)
        {
            if (transaction.SenderAddress is null)
            {
                transaction.SenderAddress = privateKey.Address;
            }
            else if (transaction.SenderAddress != privateKey.Address)
            {
                throw new T8nException("frame transaction sender does not match secretKey", T8nErrorCodes.ErrorJson);
            }
        }

        if (transaction.SenderAddress is null)
        {
            throw new T8nException("frame transaction requires a sender or a secretKey", T8nErrorCodes.ErrorJson);
        }

        if (privateKey is null || transaction.FrameSignatures is null)
        {
            return;
        }

        Ecdsa ecdsa = new();
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(transaction);

        for (int i = 0; i < transaction.FrameSignatures.Length; i++)
        {
            TxFrameSignature signature = transaction.FrameSignatures[i];
            if (!signature.Signature.IsEmpty || signature.Scheme == TxFrameSignature.SchemeArbitrary)
            {
                continue;
            }

            // TxFrameSignatureDecoder elides the signature bytes of canonical-hash entries only, so an
            // explicit-digest entry's bytes are part of the sigHash preimage: filling one here would
            // invalidate every canonical signature already produced in this loop.
            if (signature.Scheme != TxFrameSignature.SchemeSecp256k1 || !signature.SignsCanonicalHash)
            {
                throw new T8nException($"cannot fill frame signature {i}: only canonical-hash secp256k1 entries are fillable",
                    T8nErrorCodes.ErrorJson);
            }

            if (signature.Signer is not null && signature.Signer != privateKey.Address)
            {
                throw new T8nException("frame signature signer does not match secretKey", T8nErrorCodes.ErrorJson);
            }

            Signature ecdsaSignature = ecdsa.Sign(privateKey, in sigHash);
            byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
            vrs[0] = ecdsaSignature.RecoveryId;
            ecdsaSignature.Bytes.CopyTo(vrs.AsSpan(1));

            transaction.FrameSignatures[i] = new TxFrameSignature(signature.Scheme, signature.Signer, signature.Msg, vrs);
        }
    }
}
