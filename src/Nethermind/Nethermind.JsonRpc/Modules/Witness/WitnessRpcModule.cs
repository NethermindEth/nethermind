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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Witness
{
    public class WitnessRpcModule : IWitnessRpcModule
    {
        private readonly IWitnessCollector? _wrapper;

        public WitnessRpcModule(IWitnessCollector? wrapper)
        {
            _wrapper = wrapper;
        }

        public async Task<ResultWrapper<string>> get_witnesses(string n)
        {
            if (!int.TryParse(n, out int numberOfBlocks) || _wrapper is null)
                return ResultWrapper<string>.Fail("Can convert n (represent the number of witness to return) to int");
            IEnumerable<Keccak> collected = _wrapper.Collected.Skip(Math.Max(0, _wrapper.Collected.Count - numberOfBlocks));
            string result = string.Join(",", collected.Select(keccak => keccak.ToString()).ToArray());
            return ResultWrapper<string>.Success(
                result);

        }
    }
}
