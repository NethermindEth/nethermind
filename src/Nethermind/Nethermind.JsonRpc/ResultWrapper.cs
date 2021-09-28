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

using System;
using Nethermind.Core;
using Nethermind.Facade.Proxy;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class ResultWrapper<T> : IResultWrapper
    {
        public T Data { get; set; }
        public Result Result { get; set; }
        public int ErrorCode { get; set; }

        private ResultWrapper()
        {
        }
        
        public static ResultWrapper<T> Fail<TSearch>(SearchResult<TSearch> searchResult) where TSearch : class
        {
            return new() { Result = Result.Fail(searchResult.Error), ErrorCode = searchResult.ErrorCode};
        }
        
        public static ResultWrapper<T> Fail(string error)
        {
            return new() { Result = Result.Fail(error), ErrorCode = ErrorCodes.InternalError};
        }
        
        public static ResultWrapper<T> Fail(Exception e)
        {
            return new() { Result = Result.Fail(e.ToString()), ErrorCode = ErrorCodes.InternalError};
        }

        public static ResultWrapper<T> Fail(string error, int errorCode, T outputData)
        {
            return new() { Result = Result.Fail(error), ErrorCode = errorCode, Data = outputData};
        }
        
        public static ResultWrapper<T> Fail(string error, int errorCode)
        {
            return new() { Result = Result.Fail(error), ErrorCode = errorCode};
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

        public object GetData()
        {
            return Data;
        }

        public int GetErrorCode()
        {
            return ErrorCode;
        }
        
        public static ResultWrapper<T> From(RpcResult<T> rpcResult)
        {
            if (rpcResult is null)
            {
                return Fail("Missing result.");
            }

            return rpcResult.IsValid ? Success(rpcResult.Result) : Fail(rpcResult.Error.Message);
        }
    }
}
