// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
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

    public Transaction[] GetTransactions(TxDecoder decoder, ulong chainId)
    {
        List<Transaction> transactions = [];
        if (TxRlp is not null)
        {
            RlpStream rlp = new(Bytes.FromHexString(TxRlp));
            transactions = decoder.DecodeArray(rlp).ToList();
        }
        else if (Txs is not null && TransactionMetaDataList is not null)
        {

            for (int i = 0; i < Txs.Length; i++)
            {
                var transaction = Txs[i].ToTransaction();
                transaction.SenderAddress = Txs[i] is LegacyTransactionForRpc ? ((LegacyTransactionForRpc)Txs[i]).From : null;
                SignTransaction(transaction, TransactionMetaDataList[i], (LegacyTransactionForRpc) Txs[i]);

                transactions.Add(transaction);
            }
        }

        RecoverAddress(transactions, chainId);

        return transactions.ToArray();
    }

    private static void RecoverAddress(List<Transaction> transactions, ulong chainId)
    {
        var ecdsa = new EthereumEcdsa(chainId);

        foreach (Transaction transaction in transactions)
        {
            transaction.ChainId ??= chainId;
            transaction.SenderAddress ??= ecdsa.RecoverAddress(transaction);
            transaction.Hash = transaction.CalculateHash();
        }
    }

    private static void SignTransaction(Transaction transaction, TransactionMetaData transactionMetaData, LegacyTransactionForRpc txLegacy)
    {
        if (txLegacy.R.HasValue && txLegacy.S.HasValue && txLegacy.V.HasValue && txLegacy.V.Value >= 27)
        {
            transaction.Signature = new Signature(txLegacy.R.Value, txLegacy.S.Value, txLegacy.V.Value.ToUInt64(null));
        }
        else if (transactionMetaData.SecretKey is not null)
        {
            var privateKey = new PrivateKey(transactionMetaData.SecretKey);
            transaction.SenderAddress = privateKey.Address;

            EthereumEcdsa ecdsa = new(transaction.ChainId ?? TestBlockchainIds.ChainId);

            ecdsa.Sign(privateKey, transaction, transactionMetaData.Protected ?? true);
        }
    }
}
