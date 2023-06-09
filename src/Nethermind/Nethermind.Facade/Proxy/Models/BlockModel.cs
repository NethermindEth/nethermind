// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Facade.Proxy.Models;

public class BlockModel<T>
{
    public UInt256 Difficulty { get; set; }
    public byte[] ExtraData { get; set; }
    public UInt256 GasLimit { get; set; }
    public UInt256 GasUsed { get; set; }
    public Keccak Hash { get; set; }
    public Address Miner { get; set; }
    public Keccak MixHash { get; set; }
    public UInt256 Nonce { get; set; }
    public UInt256 Number { get; set; }
    public Keccak ParentHash { get; set; }
    public Keccak ReceiptsRoot { get; set; }
    public Keccak Sha3Uncles { get; set; }
    public UInt256 Size { get; set; }
    public Keccak StateRoot { get; set; }
    public ulong Timestamp { get; set; }
    public UInt256 TotalDifficulty { get; set; }
    public List<T> Transactions { get; set; }
    public Keccak TransactionsRoot { get; set; }


    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ulong? DataGasUsed { get; set; }


    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ulong? ExcessDataGas { get; set; }
}
