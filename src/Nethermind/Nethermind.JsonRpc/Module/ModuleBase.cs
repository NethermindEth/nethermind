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

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.Config;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public abstract class ModuleBase
    {
        protected readonly ILogger Logger;
        protected readonly IJsonRpcConfig ConfigurationProvider;
        protected readonly IJsonSerializer _jsonSerializer;

        protected ModuleBase(IConfigProvider configurationProvider, ILogManager logManager, IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
            Logger = logManager.GetClassLogger();
            ConfigurationProvider = configurationProvider.GetConfig<IJsonRpcConfig>();
        }

        public virtual void Initialize()
        {
        }

        protected Data Sha3(Data data)
        {
            var keccak = Keccak.Compute((byte[])data.Value);
            var keccakValue = keccak.ToString();
            return new Data(keccakValue);
        }

        protected string GetJsonLog(object model)
        {
            return _jsonSerializer.Serialize(model);
        }
    }
}