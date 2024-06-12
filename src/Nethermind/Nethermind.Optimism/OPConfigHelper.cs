// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class OPSpecHelper : IOPConfigHelper
{
    private readonly ulong _regolithTimestamp;
    private readonly long _bedrockBlockNumber;
    private readonly ulong? _canyonTimestamp;

    public Address L1FeeReceiver { get; init; }

    public OPSpecHelper(OptimismParameters parameters)
    {
        _regolithTimestamp = parameters.RegolithTimestamp;
        _bedrockBlockNumber = parameters.BedrockBlockNumber;
        _canyonTimestamp = parameters.CanyonTimestamp;
        L1FeeReceiver = parameters.L1FeeRecipient;
        Create2DeployerCode = parameters.Create2DeployerCode;
        Create2DeployerAddress = parameters.Create2DeployerAddress;
    }

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
        return header.Timestamp >= (_canyonTimestamp ?? long.MaxValue);
    }

    public Address? Create2DeployerAddress { get; }
    public byte[]? Create2DeployerCode { get; }
}
