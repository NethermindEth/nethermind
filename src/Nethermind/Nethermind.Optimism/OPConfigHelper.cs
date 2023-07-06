// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

public class OPConfigHelper : IOPConfigHelper
{
    private readonly long _regolithBlockNumber;
    private readonly long _bedrockBlockNumber;

    public Address L1FeeReceiver { get; init; }

    public OPConfigHelper(long regolithBlockNumber, long bedrockBlockNumber, Address l1FeeReceiver)
    {
        _regolithBlockNumber = regolithBlockNumber;
        _bedrockBlockNumber = bedrockBlockNumber;
        L1FeeReceiver = l1FeeReceiver;
    }

    public bool IsRegolith(BlockHeader header)
    {
        return header.Number >= _regolithBlockNumber;
    }

    public bool IsBedrock(BlockHeader header)
    {
        return header.Number >= _bedrockBlockNumber;
    }
}
