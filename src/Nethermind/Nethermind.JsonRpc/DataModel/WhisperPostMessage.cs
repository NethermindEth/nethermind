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

namespace Nethermind.JsonRpc.DataModel
{
    public class WhisperPostMessage : IJsonRpcRequest
    {
        public Data From { get; set; }
        public Data To { get; set; }
        public IEnumerable<Data> Topics { get; set; }
        public Data Payload { get; set; }
        public Quantity Priority { get; set; }
        public Quantity Ttl { get; set; }

        public virtual void FromJson(string jsonValue)
        {
            throw new NotImplementedException();
        }
    }
}