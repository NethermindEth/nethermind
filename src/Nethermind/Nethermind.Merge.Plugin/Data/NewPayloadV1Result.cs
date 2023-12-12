// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Wraps <see cref="PayloadStatusV1"/> in <see cref="ResultWrapper{T}"/> for JSON RPC.
/// </summary>
public static class NewPayloadV1Result
{
    public static ResultWrapper<PayloadStatusV1> Syncing = ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Syncing);

    public static ResultWrapper<PayloadStatusV1> Accepted = ResultWrapper<PayloadStatusV1>.Success(PayloadStatusV1.Accepted);

    public static ResultWrapper<PayloadStatusV1> Invalid(Hash256? latestValidHash, string? validationError = null)
    {
        return ResultWrapper<PayloadStatusV1>.Fail("Invalid Params", ErrorCodes.InvalidParams, new PayloadStatusV1() { Status = PayloadStatus.Invalid, LatestValidHash = latestValidHash, ValidationError = validationError });
    }

    public static ResultWrapper<PayloadStatusV1> Valid(Hash256? latestValidHash)
    {
        return ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1() { Status = PayloadStatus.Valid, LatestValidHash = latestValidHash });
    }
}
