// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Optimism.CL.Decoding;

namespace Nethermind.Optimism.CL.L1Bridge;

public interface IL1Bridge
{
    Task Initialize(CancellationToken token);
    Task<L1BridgeStepResult> Step(CancellationToken token);
    Task<L1Block> GetBlock(ulong blockNumber, CancellationToken token);
    Task<L1Block> GetBlockByHash(Hash256 blockHash, CancellationToken token);
    Task<ReceiptForRpc[]> GetReceiptsByBlockHash(Hash256 blockHash, CancellationToken token);
    void Reset(BlockId highestFinalizedOrigin);
}

public enum L1BridgeStepResultType
{
    Block,
    Finalization,
    Skip,
    Reorg,
}

public class L1BridgeStepResult
{
    public static L1BridgeStepResult Skip => new() { Type = L1BridgeStepResultType.Skip };
    public static L1BridgeStepResult Reorg => new() { Type = L1BridgeStepResultType.Reorg };

    public static L1BridgeStepResult Block(DaDataSource[] data) =>
        new() { Type = L1BridgeStepResultType.Block, NewData = data };

    public static L1BridgeStepResult Finalization(ulong blockNumber) => new() { Type = L1BridgeStepResultType.Finalization, NewFinalized = blockNumber };

    public L1BridgeStepResultType Type { private set; get; }
    public DaDataSource[]? NewData { private set; get; }
    public ulong? NewFinalized { private set; get; }

    public override string ToString() => Type switch
    {
        L1BridgeStepResultType.Block => $"L1 Event Block. Blobs count {NewData!.Length}",
        L1BridgeStepResultType.Finalization => $"L1 Event Finalization. Block Number: {NewFinalized}",
        L1BridgeStepResultType.Skip => "L1 Event Skip",
        L1BridgeStepResultType.Reorg => "L1 Event Reorg",
        _ => throw new ArgumentOutOfRangeException(nameof(Type))
    };

}
