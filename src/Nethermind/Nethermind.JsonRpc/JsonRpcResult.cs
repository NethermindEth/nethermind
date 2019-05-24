/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;

namespace Nethermind.JsonRpc
{
    public class JsonRpcResult
    {
        public bool IsCollection { get; }
        public IReadOnlyList<JsonRpcResponse> Responses { get; }

        private JsonRpcResult(bool isCollection, IReadOnlyList<JsonRpcResponse> responses)
        {
            IsCollection = isCollection;
            Responses = responses;
        }

        public static JsonRpcResult Single(JsonRpcResponse response)
            => new JsonRpcResult(false, new[] {response});

        public static JsonRpcResult Collection(IReadOnlyList<JsonRpcResponse> responses)
            => new JsonRpcResult(true, responses);
    }
}