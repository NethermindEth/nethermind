// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.L2.Common.L1Rpc;

public interface IEthApi
{
    Task<ReceiptForRpc[]?> GetReceiptsByHash(Hash256 blockHash);
    Task<L1Block?> GetBlockByHash(Hash256 blockHash, bool fullTxs);
    Task<L1Block?> GetBlockByNumber(ulong blockNumber, bool fullTxs);
    Task<L1Block?> GetHead(bool fullTxs);
    Task<L1Block?> GetFinalized(bool fullTxs);
    Task<ulong> GetChainId();
    Task<UInt256> GetBlobBaseFee();
    Task<L1FeeHistoryResults?> GetFeeHistory(int blockCount, BlockParameter newestBlock, double[]? rewardPercentiles);
}

/// <summary>
/// L1 fee history results that uses regular arrays for JSON deserialization.
/// This avoids the ArrayPoolList deserialization when calling external L1 RPC endpoints.
/// </summary>
public class L1FeeHistoryResults
{
    public UInt256[] BaseFeePerGas { get; set; } = [];
    public UInt256[] BaseFeePerBlobGas { get; set; } = [];
    public double[] GasUsedRatio { get; set; } = [];
    public double[] BlobGasUsedRatio { get; set; } = [];
    public long OldestBlock { get; set; }
    public UInt256[][]? Reward { get; set; }
}

public readonly struct L1Block
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

public readonly struct L1Transaction
{
    public Hash256? Hash { get; init; }
    public TxType? Type { get; init; }
    public Address? From { get; init; }
    public Address? To { get; init; }
    public byte[][]? BlobVersionedHashes { get; init; }
    public byte[]? Input { get; init; }
}
