// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Evm.t8n.JsonTypes;

public class InputData
{
    public Dictionary<Address, AccountState>? Alloc { get; set; }
    public EnvJson? Env { get; set; }
    public TransactionJson[]? Txs { get; set; }
    public string? TxRlp { get; set; }

    public Transaction[] GetTransactions(TxDecoder decoder)
    {
        Transaction[] transactions = [];
        if (TxRlp is not null)
        {
            RlpStream rlp = new(Bytes.FromHexString(TxRlp));
            transactions = decoder.DecodeArray(rlp);
        }
        else if (Txs is not null)
        {
            transactions = Txs.Select(txInfo => txInfo.ConvertToTx()).ToArray();
        }

        return transactions;
    }
}
