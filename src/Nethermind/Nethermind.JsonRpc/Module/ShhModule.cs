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
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public class ShhModule : ModuleBase, IShhModule
    {
        public ShhModule(IConfigProvider configurationProvider, ILogManager logManager, IJsonSerializer jsonSerializer) : base(configurationProvider, logManager, jsonSerializer)
        {
        }

        public ResultWrapper<bool> shh_post(WhisperPostMessage message)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<Data> shh_newIdentity()
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<bool> shh_hasIdentity(Data address)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<Quantity> shh_newFilter(WhisperFilter filter)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<bool> shh_uninstallFilter(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<IEnumerable<WhisperMessage>> shh_getFilterChanges(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<IEnumerable<WhisperMessage>> shh_getMessages(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }
    }
}