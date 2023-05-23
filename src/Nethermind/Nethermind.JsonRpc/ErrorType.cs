// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        /// <summary>
        /// Unknown block error
        /// </summary>
        public const int UnknownBlockError = -39001;

    }
}
