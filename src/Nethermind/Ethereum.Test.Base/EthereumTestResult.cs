// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Int256;

namespace Ethereum.Test.Base
{
    public class EthereumTestResult
    {
        public EthereumTestResult(string? name, string? fork, bool pass)
        {
            Name = name ?? "unnamed";
            Fork = fork ?? "unknown";
            Pass = pass;
        }

        public EthereumTestResult(string? name, string? fork, string loadFailure)
        {
            Name = name ?? "unnamed";
            Fork = fork ?? "unknown";
            Pass = false;
            LoadFailure = loadFailure;
        }

        public EthereumTestResult(string? name, string? loadFailure)
            : this(name, null, loadFailure)
        {
        }

        public string? LoadFailure { get; set; }
        public string Name { get; set; }
        public bool Pass { get; set; }
        public string Fork { get; set; }

        public double TimeInMs { get; set; }

        public Hash256 StateRoot { get; set; } = Keccak.EmptyTreeHash;
        
        public Hash256? TxRoot { get; set; }
        public Hash256? ReceiptsRoot { get; set; }
        public Hash256? WithdrawalsRoot { get; set; }
        public Hash256? LogsHash { get; set; }
        public Bloom? LogsBloom { get; set; }
        public TxReceipt[]? Receipts { get; set; }
        public RejectedTx[]? Rejected { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public UInt256? CurrentDifficulty { get; set; }
        public UInt256? GasUsed { get; set; }
        public UInt256? CurrentBaseFee { get; set; }
        public UInt256? CurrentExcessBlobGas { get; set; }
        public UInt256? BlobGasUsed { get; set; }
        public Dictionary<Address, NativePrestateTracerAccount> Accounts { get; set; }
        public byte[] TransactionsRlp { get; set; }
    }
}

public class RejectedTx
{
    public RejectedTx(int index, string error)
    {
        Index = index;
        Error = error;
    }

    public int Index { get; set; }
    public string? Error { get; set; }
}

