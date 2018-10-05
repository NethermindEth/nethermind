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

using Nethermind.Core;

namespace Nethermind.JsonRpc.DataModel
{
    public class CompilerParameters : IJsonRpcRequest
    {
        private readonly IJsonSerializer _jsonSerializer;

        public CompilerParameters()
        {
            _jsonSerializer = new UnforgivingJsonSerializer();
        }

        public string Contract { get; set; }
        public string EvmVersion { get; set; }
        public bool Optimize { get; set; }
        public uint? Runs { get; set; }
        
        public void FromJson(string jsonValue)
        {
            var jsonObj = new
            {
                contract = string.Empty,
                evmversion = string.Empty,
                optimize = new bool(),
                runs = new uint()
            };

            var compileParameters = _jsonSerializer.DeserializeAnonymousType(jsonValue, jsonObj);
            Contract = compileParameters.contract;
            EvmVersion = compileParameters.evmversion ?? "byzantium";
            Optimize = compileParameters.optimize;
            Runs = compileParameters.runs;
        }

        public string ToJson()
        {
            return _jsonSerializer.Serialize(this);
        }
    }
}