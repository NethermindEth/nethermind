// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class OptimismSpecHelper(OptimismParameters parameters) : IOptimismSpecHelper
{
    private readonly long _bedrockBlockNumber = parameters.BedrockBlockNumber;
    private readonly ulong _regolithTimestamp = parameters.RegolithTimestamp;
    private readonly ulong? _canyonTimestamp = parameters.CanyonTimestamp;
    private readonly ulong? _ecotoneTimestamp = parameters.EcotoneTimestamp;
    private readonly ulong? _fjordTimestamp = parameters.FjordTimestamp;

    public Address L1FeeReceiver { get; init; } = parameters.L1FeeRecipient;

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

    public bool IsEcotone(BlockHeader header)
    {
        return header.Timestamp >= _ecotoneTimestamp;
    }

    public bool IsFjord(BlockHeader header)
    {
        return header.Timestamp >= _fjordTimestamp;
    }

    public Address? Create2DeployerAddress { get; } = parameters.Create2DeployerAddress;
    public byte[]? Create2DeployerCode { get; } = parameters.Create2DeployerCode;
}
