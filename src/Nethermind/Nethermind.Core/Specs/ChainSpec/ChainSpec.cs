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
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.ChainSpec
{
    /// <summary>
    /// TODO: ChainSpec files (.json) were copied from Parity repository (https://github.com/paritytech/parity), need to review 
    /// </summary>
    [DebuggerDisplay("{Name}, ChainId = {ChainId}")]
    public class ChainSpec
    {
        public Dictionary<Address, UInt256> Allocations { get; set; }
        public NetworkNode[] Bootnodes { get; set; }
        public Block Genesis { get; set; }

        /// <summary>
        /// Not used in Nethermind
        /// </summary>
        public string DataDir { get; set; }

        public SealEngineType SealEngineType { get; set; }

        public Dictionary<string, string> SealEngineParams { get; set; }

        public T ReadSealEngineParam<T>(string name)
        {
            if (SealEngineParams.ContainsKey(name))
            {
                if (SealEngineParams[name] is string potentiallyHexString)
                {
                    if (potentiallyHexString.StartsWith("0x"))
                    SealEngineParams[name] = UInt256.Parse(potentiallyHexString.Replace("0x", string.Empty), NumberStyles.HexNumber).ToString();
                }
                
                return (T) Convert.ChangeType(SealEngineParams[name], typeof(T));
            }

            return default;
        }

        public string Name { get; set; }

        public int ChainId { get; set; }
        // TODO: seal engine - for now we support PoW ethash only
    }
}