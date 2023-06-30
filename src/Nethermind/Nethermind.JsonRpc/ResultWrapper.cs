// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Proxy;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class ResultWrapper<T> : IResultWrapper
    {
        object IResultWrapper.Data => Data;
        public T Data { get; init; }
        public Result Result { get; init; } = Result.Success;
        public int ErrorCode { get; init; }
        public bool IsTemporary { get; init; }

        private ResultWrapper()
        {
        }

        public static ResultWrapper<T> Fail<TSearch>(SearchResult<TSearch> searchResult, bool isTemporary = false) where TSearch : class =>
            new() { Result = Result.Fail(searchResult.Error!), ErrorCode = searchResult.ErrorCode, IsTemporary = isTemporary };

        public static ResultWrapper<T> Fail(string error) =>
            new() { Result = Result.Fail(error), ErrorCode = ErrorCodes.InternalError };

        public static ResultWrapper<T> Fail(Exception e) =>
            new() { Result = Result.Fail(e.ToString()), ErrorCode = ErrorCodes.InternalError };

        public static ResultWrapper<T> Fail(string error, int errorCode, T outputData) =>
            new() { Result = Result.Fail(error), ErrorCode = errorCode, Data = outputData };

        public static ResultWrapper<T> Fail(string error, int errorCode, bool isTemporary = false) =>
            new() { Result = Result.Fail(error), ErrorCode = errorCode, IsTemporary = isTemporary };

        public static ResultWrapper<T> Fail(string error, T data) =>
            new() { Data = data, Result = Result.Fail(error) };

        public static ResultWrapper<T> Success(T data) =>
            new() { Data = data, Result = Result.Success };

        public static ResultWrapper<T> From(RpcResult<T>? rpcResult) =>
            rpcResult is null
                ? Fail("Missing result.")
                : rpcResult.IsValid ? Success(rpcResult.Result) : Fail(rpcResult.Error.Message);

        public static implicit operator Task<ResultWrapper<T>>(ResultWrapper<T> resultWrapper) => Task.FromResult(resultWrapper);
    }
}
