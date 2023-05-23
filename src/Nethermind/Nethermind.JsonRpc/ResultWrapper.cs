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
        public T Data { get; set; }
        public Result Result { get; set; } = null!;
        public int ErrorCode { get; set; }

        private ResultWrapper()
        {
        }

        public static ResultWrapper<T> Fail<TSearch>(SearchResult<TSearch> searchResult) where TSearch : class
        {
            return new() { Result = Result.Fail(searchResult.Error), ErrorCode = searchResult.ErrorCode };
        }

        public static ResultWrapper<T> Fail(string error)
        {
            return new() { Result = Result.Fail(error), ErrorCode = ErrorCodes.InternalError };
        }

        public static ResultWrapper<T> Fail(Exception e)
        {
            return new() { Result = Result.Fail(e.ToString()), ErrorCode = ErrorCodes.InternalError };
        }

        public static ResultWrapper<T> Fail(string error, int errorCode, T outputData)
        {
            return new() { Result = Result.Fail(error), ErrorCode = errorCode, Data = outputData };
        }

        public static ResultWrapper<T> Fail(string error, int errorCode)
        {
            return new() { Result = Result.Fail(error), ErrorCode = errorCode };
        }

        public static ResultWrapper<T> Fail(string error, T data)
        {
            return new() { Data = data, Result = Result.Fail(error) };
        }

        public static ResultWrapper<T> Success(T data)
        {
            return new() { Data = data, Result = Result.Success };
        }

        public Result GetResult()
        {
            return Result;
        }

        public object? GetData()
        {
            return Data;
        }

        public int GetErrorCode()
        {
            return ErrorCode;
        }

        public static ResultWrapper<T> From(RpcResult<T>? rpcResult)
        {
            if (rpcResult is null)
            {
                return Fail("Missing result.");
            }

            return rpcResult.IsValid ? Success(rpcResult.Result) : Fail(rpcResult.Error.Message);
        }

        public static implicit operator Task<ResultWrapper<T>>(ResultWrapper<T> resultWrapper) => Task.FromResult(resultWrapper);
    }
}
