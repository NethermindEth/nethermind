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
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Evm.Tracing.Access
{
    public class AccessBlockTracer : BlockTracerBase<AccessTxTracer, AccessTxTracer>
    {
        private readonly Address[] _addressesToOptimize;
        private IList<Address> _addressesAccessed = new List<Address>();

        public Address[] AddressesAccessed => _addressesAccessed.ToArray();

        public AccessBlockTracer(Address[] addressesToOptimize)
        {
            _addressesToOptimize = addressesToOptimize;
        }

        protected override AccessTxTracer OnStart(Transaction? tx) => new(_addressesToOptimize);

        protected override AccessTxTracer OnEnd(AccessTxTracer txTracer)
        {
            if (txTracer.AccessList is not null)
            {
                foreach (Address address in txTracer.AccessList?.Data.Keys)
                {
                    _addressesAccessed.Add(address);
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
