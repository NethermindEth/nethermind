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
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc
{
    public class ConfigurationProvider : IConfigurationProvider
    {
        public ConfigurationProvider()
        {
            EnabledModules = Enum.GetValues(typeof(ModuleType)).OfType<ModuleType>();
        }

        public IDictionary<ErrorType, int> ErrorCodes => new Dictionary<ErrorType, int>
        {
            { ErrorType.ParseError, -32700 },
            { ErrorType.InvalidRequest, -32600 },
            { ErrorType.MethodNotFound, -32601 },
            { ErrorType.InvalidParams, -32602 },
            { ErrorType.InternalError, -32603 }
        };

        public string JsonRpcVersion => "2.0";
        public IEnumerable<ModuleType> EnabledModules { get; set; }
        public Encoding MessageEncoding => Encoding.UTF8;
        public string SignatureTemplate => "\x19Ethereum Signed Message:\n{0}{1}";
    }
}