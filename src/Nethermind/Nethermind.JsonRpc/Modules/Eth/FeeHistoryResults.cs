// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Eth;

public class FeeHistoryResults
{
    public UInt256[]? BaseFeePerGas { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256[]? BaseFeePerBlobGas { get; }

    public double[]? GasUsedRatio { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? BlobGasUsedRatio { get; }

    public long OldestBlock { get; }
    public UInt256[][]? Reward { get; }

    public FeeHistoryResults(long oldestBlock, UInt256[] baseFeePerGas, double[] gasUsedRatio, UInt256[] baseFeePerBlobGas, double[] blobGasUsedRatio, UInt256[][]? reward = null)
    {
        OldestBlock = oldestBlock;
        Reward = reward;
        BaseFeePerGas = baseFeePerGas;
        BaseFeePerBlobGas = baseFeePerBlobGas;
        GasUsedRatio = gasUsedRatio;
        BlobGasUsedRatio = blobGasUsedRatio;
    }
}
