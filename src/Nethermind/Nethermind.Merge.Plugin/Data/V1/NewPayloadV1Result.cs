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

using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Data.V1;

/// <summary>
/// Wraps <see cref="PayloadStatusV1"/> in <see cref="ResultWrapper{T}"/> for JSON RPC.
/// </summary>
public static class NewPayloadV1Result
{
    public static ResultWrapper<PayloadStatusV1> Syncing = ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Syncing);

    public static ResultWrapper<PayloadStatusV1> InvalidBlockHash = ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.InvalidBlockHash);

    public static ResultWrapper<PayloadStatusV1> Accepted = ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Accepted);

    public static ResultWrapper<PayloadStatusV1> Invalid(Keccak? latestValidHash, string? validationError = null)
    {
        return ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1() { Status = PayloadStatus.Invalid, LatestValidHash = latestValidHash, ValidationError = validationError });
    }

    public static ResultWrapper<PayloadStatusV1> Valid(Keccak? latestValidHash)
    {
        return ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1() { Status = PayloadStatus.Valid, LatestValidHash = latestValidHash });
    }
}
