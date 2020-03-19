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