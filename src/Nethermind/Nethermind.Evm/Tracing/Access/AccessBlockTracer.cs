//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.Access
{
    public class AccessBlockTracer : BlockTracerBase<AccessTxTracer, AccessTxTracer>
    {
        private readonly Address[] _addressesToOptimize;
        private IDictionary<Address, HashSet<UInt256>> _accessListData = new Dictionary<Address, HashSet<UInt256>>();

        public AccessList AccessList => new(_accessListData as IReadOnlyDictionary<Address, IReadOnlySet<UInt256>>);

        public AccessBlockTracer(Address[] addressesToOptimize)
        {
            _addressesToOptimize = addressesToOptimize;
        }

        protected override AccessTxTracer OnStart(Transaction? tx) => new(_addressesToOptimize);

        protected override AccessTxTracer OnEnd(AccessTxTracer txTracer)
        {
            if (txTracer.AccessList is not null)
            {
                IReadOnlyDictionary<Address, IReadOnlySet<UInt256>> accessListData = txTracer.AccessList.Data;
                foreach (Address address in accessListData.Keys)
                {
                    if (_accessListData.ContainsKey(address))
                    {
                        _accessListData[address].UnionWith(accessListData[address]);
                    }
                    else
                    {
                        _accessListData.Add(address, new HashSet<UInt256>(accessListData[address]));
                    }
                }
            }
            return txTracer;
        }

        public override void StartNewBlockTrace(Block block)
        {
        }

        public override void EndBlockTrace()
        {
        }
    }
}
