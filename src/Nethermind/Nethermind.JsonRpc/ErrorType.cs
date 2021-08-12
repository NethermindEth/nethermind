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

namespace Nethermind.JsonRpc
{
    public static class ErrorCodes
    {
        public const int None = 0;

        /// <summary>
        /// Invalid JSON
        /// </summary>
        public const int ParseError = -32700;

        /// <summary>
        /// JSON is not a valid request object
        /// </summary>
        public const int InvalidRequest = -32600;

        /// <summary>
        /// Method does not exist
        /// </summary>
        public const int MethodNotFound = -32601;

        /// <summary>
        /// Invalid method parameters
        /// </summary>
        public const int InvalidParams = -32602;

        /// <summary>
        /// Internal JSON-RPC error
        /// </summary>
        public const int InternalError = -32603;

        /// <summary>
        /// Missing or invalid parameters
        /// </summary>
        public const int InvalidInput = -32000;

        /// <summary>
        /// Requested resource not found
        /// </summary>
        public const int ResourceNotFound = -32001;
        
        /// <summary>
        /// Requested resource not available
        /// </summary>
        public const int ResourceUnavailable = -32002;
        
        /// <summary>
        /// Transaction creation failed
        /// </summary>
        public const int TransactionRejected = -32010;
        
        /// <summary>
        /// Account locked
        /// </summary>
        public const int AccountLocked = -32020;
        
        /// <summary>
        /// Method is not implemented
        /// </summary>
        public const int MethodNotSupported = -32004;
        
        /// <summary>
        /// Request exceeds defined limit
        /// </summary>
        public const int LimitExceeded = -32005;

        /// <summary>
        /// Version of JSON-RPC protocol is not supported
        /// </summary>
        public const int RpcVersionNotSupported = -32015;
        
        /// <summary>
        /// 
        /// </summary>
        public const int ExecutionError = -32015;
         
        /// <summary>
        /// Request exceeds defined timeout limit
        /// </summary>
        public const int Timeout = -32016;
        
        /// <summary>
        /// Request exceeds defined timeout limit
        /// </summary>
        public const int ModuleTimeout = -32017;
    }
}
