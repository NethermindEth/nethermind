// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Overseer.Test.JsonRpc
{
    public class JsonRpcResponse<T>
    {
        public int Id { get; set; }
        public string JsonRpc { get; set; }
        public T Result { get; set; }
        public ErrorResponse Error { get; set; }
        public bool IsValid => Error is null;

        public class ErrorResponse
        {
            public ErrorResponse(int code, string message, object data = null)
            {
                Code = code;
                Message = message;
            }

            public int Code { get; set; }
            public string Message { get; set; }
            public object Data { get; set; }
        }
    }
}
