// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy
{
    public class RpcResult<T>
    {
        public long Id { get; set; }
        public T Result { get; set; }
        public RpcError Error { get; set; }
        public bool IsValid => Error is null;

        public static RpcResult<T> Ok(T result, int id = 0) => new()
        {
            Result = result,
            Id = id
        };

        public static RpcResult<T> Fail(string message) => new()
        {
            Error = new RpcError
            {
                Message = message
            }
        };

        public class RpcError
        {
            public long Code { get; set; }
            public string Message { get; set; }
        }
    }
}
