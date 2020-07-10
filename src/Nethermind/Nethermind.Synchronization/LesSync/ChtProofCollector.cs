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

using Nethermind.State.Proofs;
using Nethermind.Trie;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Nethermind.Synchronization.LesSync
{
    class ChtProofCollector: ProofCollector
    {
        long _fromLevel;
        long _level;
        public ChtProofCollector(byte[] key, long fromLevel): base(key)
        {
            _fromLevel = fromLevel;
            _level = 0;
        }

        protected override void AddProofBits(TrieNode node)
        {
            if (_level < _fromLevel)
            {
                _level++;
            }
            else
            {
                base.AddProofBits(node);
            }
        }
    }
}
