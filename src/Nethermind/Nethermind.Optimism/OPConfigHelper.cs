// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

public class OPSpecHelper : IOPConfigHelper
{
    private readonly ulong _regolithTimestamp;
    private readonly long _bedrockBlockNumber;

    public Address L1FeeReceiver { get; init; }

    public OPSpecHelper(ulong regolithTimestamp, long bedrockBlockNumber, Address l1FeeReceiver)
    {
        _regolithTimestamp = regolithTimestamp;
        _bedrockBlockNumber = bedrockBlockNumber;
        L1FeeReceiver = l1FeeReceiver;
    }

    public bool IsRegolith(BlockHeader header)
    {
        return header.Timestamp >= _regolithTimestamp;
    }

    public bool IsBedrock(BlockHeader header)
    {
        return header.Number >= _bedrockBlockNumber;
    }
}
