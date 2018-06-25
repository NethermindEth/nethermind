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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nethermind.Config
{
    public class JsonRpcConfig : IJsonRpcConfig
    {
        public IDictionary<ConfigErrorType, int> ErrorCodes => new Dictionary<ConfigErrorType, int>
        {
            { ConfigErrorType.ParseError, -32700 },
            { ConfigErrorType.InvalidRequest, -32600 },
            { ConfigErrorType.MethodNotFound, -32601 },
            { ConfigErrorType.InvalidParams, -32602 },
            { ConfigErrorType.InternalError, -32603 }
        };

        public string JsonRpcVersion { get; set; } = "2.0";
        public IEnumerable<ConfigJsonRpcModuleType> EnabledModules { get; set; } = Enum.GetValues(typeof(ConfigJsonRpcModuleType)).OfType<ConfigJsonRpcModuleType>();
        public string MessageEncoding { get; set; } = "UTF-8";
        public string SignatureTemplate { get; set; } = "\x19Ethereum Signed Message:\n{0}{1}";
    }
}