// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.L1Bridge;

public interface IEthApi
{
    Task<ReceiptForRpc[]?> GetReceiptsByHash(Hash256 blockHash);
    Task<L1Block?> GetBlockByHash(Hash256 blockHash, bool fullTxs);
    Task<L1Block?> GetBlockByNumber(ulong blockNumber, bool fullTxs);
    Task<L1Block?> GetHead(bool fullTxs);
    Task<L1Block?> GetFinalized(bool fullTxs);
}

public struct L1Block
{
    public byte[] ExtraData { get; set; }
    public Hash256 Hash { get; set; }
    public Hash256 ParentHash { get; set; }
    public UInt256 Timestamp { get; set; }
    public L1Transaction[]? Transactions { get; set; }
    public ulong Number { get; set; }
    public Hash256? ParentBeaconBlockRoot { get; set; }
    public ulong? ExcessBlobGas { get; set; }
    public UInt256? BaseFeePerGas { get; set; }
    public Hash256 MixHash { get; set; }
}

public struct L1Transaction
{
    public Hash256? Hash { get; set; }
    public TxType? Type { get; set; }
    public Address? From { get; set; }
    public Address? To { get; set; }
    public byte[][]? BlobVersionedHashes { get; set; }
}
