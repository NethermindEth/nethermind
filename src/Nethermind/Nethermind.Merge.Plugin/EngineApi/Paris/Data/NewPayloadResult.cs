// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.EngineApi.Paris.Data;

/// <summary>
/// Wraps <see cref="PayloadStatusV1"/> in <see cref="ResultWrapper{T}"/> for JSON RPC.
/// </summary>
public abstract class NewPayloadResult<T> where T : IPayloadStatus<T>
{
    public static ResultWrapper<T> Syncing { get; } = ResultWrapper<T>.Success(T.Syncing);

    public static ResultWrapper<T> InvalidBlockHash { get; } = ResultWrapper<T>.Success(T.InvalidBlockHash);

    public static ResultWrapper<T> Accepted { get; } = ResultWrapper<T>.Success(T.Accepted);

    public static ResultWrapper<T> Invalid(Keccak? latestValidHash, string? validationError = null) =>
        ResultWrapper<T>.Success(T.Invalid(latestValidHash, validationError));

    public static ResultWrapper<T> Valid(Keccak? latestValidHash) =>
        ResultWrapper<T>.Success(T.Valid(latestValidHash));
}
