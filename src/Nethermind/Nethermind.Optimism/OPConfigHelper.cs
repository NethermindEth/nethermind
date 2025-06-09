// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

public class OptimismSpecHelper(OptimismChainSpecEngineParameters parameters) : IOptimismSpecHelper
{
    private readonly long? _bedrockBlockNumber = parameters.BedrockBlockNumber;
    private readonly ulong? _regolithTimestamp = parameters.RegolithTimestamp;
    private readonly ulong? _canyonTimestamp = parameters.CanyonTimestamp;
    private readonly ulong? _deltaTimestamp = parameters.DeltaTimestamp;
    private readonly ulong? _ecotoneTimestamp = parameters.EcotoneTimestamp;
    private readonly ulong? _fjordTimestamp = parameters.FjordTimestamp;
    private readonly ulong? _graniteTimestamp = parameters.GraniteTimestamp;
    private readonly ulong? _holoceneTimestamp = parameters.HoloceneTimestamp;
    private readonly ulong? _isthmusTimestamp = parameters.IsthmusTimestamp;

    public Address? L1FeeReceiver { get; init; } = parameters.L1FeeRecipient;

    public bool IsRegolith(BlockHeader header)
    {
        return header.Timestamp >= _regolithTimestamp;
    }

    public bool IsBedrock(BlockHeader header)
    {
        return header.Number >= _bedrockBlockNumber;
    }

    public bool IsCanyon(BlockHeader header)
    {
        return header.Timestamp >= _canyonTimestamp;
    }

    public bool IsDelta(BlockHeader header)
    {
        return header.Timestamp >= _deltaTimestamp;
    }

    public bool IsEcotone(BlockHeader header)
    {
        return header.Timestamp >= _ecotoneTimestamp;
    }

    public bool IsFjord(BlockHeader header)
    {
        return header.Timestamp >= _fjordTimestamp;
    }

    public bool IsGranite(BlockHeader header)
    {
        return header.Timestamp >= _graniteTimestamp;
    }

    public bool IsHolocene(BlockHeader header)
    {
        return header.Timestamp >= _holoceneTimestamp;
    }

    public bool IsIsthmus(BlockHeader header)
    {
        return header.Timestamp >= _isthmusTimestamp;
    }

    public Address? Create2DeployerAddress { get; } = parameters.Create2DeployerAddress;
    public byte[]? Create2DeployerCode { get; } = parameters.Create2DeployerCode;
}
