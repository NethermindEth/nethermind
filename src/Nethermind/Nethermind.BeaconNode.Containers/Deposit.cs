﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class Deposit
    {
        private readonly List<Hash32> _proof;

        public Deposit(IEnumerable<Hash32> proof, DepositData data)
        {
            _proof = new List<Hash32>(proof);
            Data = data;
        }

        public DepositData Data { get; }

        public IReadOnlyList<Hash32> Proof => _proof.AsReadOnly();

        public override string ToString()
        {           
            return $"I:{Proof[^1].ToString().Substring(0, 12)} P:{Data.PublicKey.ToString().Substring(0, 12)} A:{Data.Amount}";
        }
    }
}
