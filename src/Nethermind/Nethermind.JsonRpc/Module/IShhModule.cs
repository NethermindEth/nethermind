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
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public interface IShhModule : IModule
    {
        ResultWrapper<bool> shh_post(WhisperPostMessage message);
        ResultWrapper<Data> shh_newIdentity();
        ResultWrapper<bool> shh_hasIdentity(Data address);
        ResultWrapper<Quantity> shh_newFilter(WhisperFilter filter);
        ResultWrapper<bool> shh_uninstallFilter(Quantity filterId);
        ResultWrapper<IEnumerable<WhisperMessage>> shh_getFilterChanges(Quantity filterId);
        ResultWrapper<IEnumerable<WhisperMessage>> shh_getMessages(Quantity filterId);
    }
}