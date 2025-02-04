// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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

    public Transaction[] GetTransactions(TxDecoder decoder, ulong chainId, IReleaseSpec spec)
    {
        var txValidator = new TxValidator(chainId);
        EthereumEcdsa ecdsa = new(chainId);

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
                SignTransaction(transaction, TransactionMetaDataList[i], (LegacyTransactionForRpc) Txs[i], txValidator, spec, ecdsa);

                transactions.Add(transaction);
            }
        }

        return transactions.ToArray();
    }

    private static void SignTransaction(Transaction transaction, TransactionMetaData transactionMetaData,
        LegacyTransactionForRpc txLegacy, TxValidator txValidator, IReleaseSpec spec, EthereumEcdsa ecdsa)
    {
        transaction.ChainId = ecdsa.ChainId;

        if (txLegacy.R.HasValue && txLegacy.S.HasValue && txLegacy.V.HasValue && txLegacy.V.Value >= 27)
        {
            transaction.Signature = new Signature(txLegacy.R.Value, txLegacy.S.Value, txLegacy.V.Value.ToUInt64(null));
            transaction.SenderAddress ??= ecdsa.RecoverAddress(transaction);
        }

        if (!txValidator.IsWellFormed(transaction, spec) && transactionMetaData.SecretKey is not null)
        {
            var privateKey = new PrivateKey(transactionMetaData.SecretKey);
            transaction.SenderAddress = privateKey.Address;


            ecdsa.Sign(privateKey, transaction, transactionMetaData.Protected ?? true);
        }

        transaction.Hash = transaction.CalculateHash();
    }
}
