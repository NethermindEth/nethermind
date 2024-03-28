// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Int256;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

namespace Nethermind.JsonRpc.Modules.Eth;

public class FeeHistoryResults(
    long oldestBlock,
    ArrayPoolList<UInt256> baseFeePerGas,
    ArrayPoolList<double> gasUsedRatio,
    ArrayPoolList<UInt256> baseFeePerBlobGas,
    ArrayPoolList<double> blobGasUsedRatio,
    ArrayPoolList<ArrayPoolList<UInt256>>? reward = null)
    : IDisposable
{
    public ArrayPoolList<UInt256> BaseFeePerGas { get; } = baseFeePerGas;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArrayPoolList<UInt256> BaseFeePerBlobGas { get; } = baseFeePerBlobGas;

    public ArrayPoolList<double> GasUsedRatio { get; } = gasUsedRatio;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArrayPoolList<double> BlobGasUsedRatio { get; } = blobGasUsedRatio;

    public long OldestBlock { get; } = oldestBlock;
    public ArrayPoolList<ArrayPoolList<UInt256>>? Reward { get; } = reward;

    public void Dispose()
    {
        BaseFeePerGas.Dispose();
        BaseFeePerBlobGas.Dispose();
        GasUsedRatio.Dispose();
        BlobGasUsedRatio.Dispose();
        if (Reward == null) return;
        foreach (ArrayPoolList<UInt256> item in Reward)
        {
            item.Dispose();
        }

        Reward.Dispose();
    }
}
