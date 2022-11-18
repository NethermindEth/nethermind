//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.Data.V2;

public class GetPayloadV2Result
{
    public ExecutionPayloadV1 ExecutionPayloadV1;
    public UInt256 BlockValue;

    public GetPayloadV2Result(Block block, UInt256 blockFees)
    {
        ExecutionPayloadV1 = new(block);
        BlockValue = blockFees;
    }

    public override string ToString() => $"ExecutionPayloadV1: {ExecutionPayloadV1}, Fees: {BlockValue}";
}
