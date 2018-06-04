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
using System.Diagnostics;
using System.Numerics;

namespace Nethermind.Core.Specs.ChainSpec
{
    /// <summary>
    /// TODO: ChainSpec files (.json) were copied from Parity repository (https://github.com/paritytech/parity), need to review 
    /// </summary>
    [DebuggerDisplay("{Name}, ChainId = {ChainId}")]
    public class ChainSpec
    {
        public Dictionary<Address, BigInteger> Allocations { get; set; }
        public NetworkNode[] NetworkNodes { get; set; }
        public Block Genesis { get; set; }

        public string Name { get; set; }

        public int ChainId { get; set; }
        // TODO: seal engine - for now we support PoW ethash only
    }
}