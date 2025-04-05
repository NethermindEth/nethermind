// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    public byte[] ExtraData { get; init; }
    public Hash256 Hash { get; init; }
    public Hash256 ParentHash { get; init; }
    public UInt256 Timestamp { get; init; }
    public L1Transaction[]? Transactions { get; init; }
    public ulong Number { get; init; }
    public Hash256? ParentBeaconBlockRoot { get; init; }
    public ulong? ExcessBlobGas { get; init; }
    public UInt256? BaseFeePerGas { get; init; }
    public Hash256 MixHash { get; init; }
}

public struct L1Transaction
{
    public Hash256? Hash { get; init; }
    public TxType? Type { get; init; }
    public Address? From { get; init; }
    public Address? To { get; init; }
    public byte[][]? BlobVersionedHashes { get; init; }
}
