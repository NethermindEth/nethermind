//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core.Model;
using Nethermind.Facade.Proxy;

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
        
        public static ResultWrapper<T> Fail(string error)
        {
            return new ResultWrapper<T> { Result = Result.Fail(error), ErrorCode = ErrorCodes.InternalError};
        }

        public static ResultWrapper<T> Fail(string error, int errorCode, T outputData)
        {
            return new ResultWrapper<T> { Result = Result.Fail(error), ErrorCode = errorCode, Data = outputData};
        }
        
        public static ResultWrapper<T> Fail(string error, int errorCode)
        {
            return new ResultWrapper<T> { Result = Result.Fail(error), ErrorCode = errorCode};
        }

        public static ResultWrapper<T> Success(T data)
        {
            return new ResultWrapper<T> { Data = data, Result = Result.Success };
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