// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core2.Api
{
    public class ApiResponse
    {
        public ApiResponse(StatusCode statusCode)
        {
            StatusCode = statusCode;
        }

        public static ApiResponse<T> Create<T>(StatusCode statusCode, T content)
        {
            return new ApiResponse<T>(statusCode, content);
        }

        public static ApiResponse<T> Create<T>(StatusCode statusCode)
        {
            return new ApiResponse<T>(statusCode, default!);
        }

        public StatusCode StatusCode { get; }
    }

    public class ApiResponse<T> : ApiResponse
    {
        public ApiResponse(StatusCode statusCode, T content) : base(statusCode)
        {
            Content = content;
        }

        public T Content { get; }
    }
}
