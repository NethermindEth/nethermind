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
using System.Text;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using ILogger = Nethermind.Core.Logging.ILogger;

namespace Nethermind.Clique
{
    public class Snapshot : ICloneable
    {
        public UInt256 Number { get; set; }
        public Keccak Hash { get; set; }
        public SortedList<Address, UInt256> Signers { get; }
        
        public List<Vote> Votes;
        internal Dictionary<Address, Tally> Tally { get; }

        internal Snapshot(UInt256 number, Keccak hash, SortedList<Address, UInt256> signers, Dictionary<Address, Tally> tally)
        {
            Number = number;
            Hash = hash;
            Signers = new SortedList<Address, UInt256>(signers, CliqueAddressComparer.Instance);
            Votes = new List<Vote>();
            Tally = tally;
        }

        internal Snapshot(UInt256 number, Keccak hash, SortedList<Address, UInt256> signers)
            : this(number, hash, signers, new Dictionary<Address, Tally>())
        {
        }

        public object Clone()
        {
            Snapshot clone = new Snapshot(Number, Hash, new SortedList<Address, UInt256>(Signers, CliqueAddressComparer.Instance), new Dictionary<Address, Tally>(Tally));
            clone.Votes = new List<Vote>(Votes);
            return clone;
        }
        
        public ulong SignerLimit => (ulong) Signers.Count / 2 + 1;
    }
}