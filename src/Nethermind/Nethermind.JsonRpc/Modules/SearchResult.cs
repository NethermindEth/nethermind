// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.JsonRpc.Modules
{
    public struct SearchResult<T> where T : class
    {
        public SearchResult(string error, int errorCode)
        {
            Object = null;
            Error = error;
            ErrorCode = errorCode;
        }

        public SearchResult(T @object)
        {
            Object = @object;
            Error = null;
            ErrorCode = 0;
        }

        [MemberNotNullWhen(false, nameof(IsError))]
        public T? Object { get; set; }

        public string? Error { get; set; }

        public int ErrorCode { get; set; }

        public bool IsError => ErrorCode != 0;
    }
}
