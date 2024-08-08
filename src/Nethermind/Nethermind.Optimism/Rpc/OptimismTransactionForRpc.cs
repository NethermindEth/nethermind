// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using System.Text.Json.Serialization;
using Nethermind.Core.Optimism;

namespace Nethermind.Optimism.Rpc;

public class OptimismTransactionForRpc : TransactionForRpc
{
    public OptimismTransactionForRpc(Hash256? blockHash, OptimismTxReceipt? receipt, Transaction transaction, UInt256? baseFee = null)
       : base(blockHash, receipt?.BlockNumber, receipt?.Index, transaction, baseFee)
    {
        if (transaction is DepositTransaction depositTransaction)
        {
            SourceHash = depositTransaction.SourceHash;
            Mint = depositTransaction.Mint;
            IsSystemTx = depositTransaction.IsOPSystemTransaction ? true : null;
            Nonce = receipt?.DepositNonce;
            DepositReceiptVersion = receipt?.DepositReceiptVersion;
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? DepositReceiptVersion { get; set; }
}
