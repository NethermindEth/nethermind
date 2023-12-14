// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

public class OPSpecHelper : IOPConfigHelper
{
    private readonly ulong _regolithTimestamp;
    private readonly long _bedrockBlockNumber;
    private readonly ulong _canyonTimestamp;

    public Address L1FeeReceiver { get; init; }

    public OPSpecHelper(ulong regolithTimestamp, long bedrockBlockNumber, ulong canyonTimestamp, Address l1FeeReceiver)
    {
        _regolithTimestamp = regolithTimestamp;
        _bedrockBlockNumber = bedrockBlockNumber;
        _canyonTimestamp = canyonTimestamp;
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

    public bool IsCanyon(BlockHeader header)
    {
        return header.Timestamp >= _canyonTimestamp;
    }
}
