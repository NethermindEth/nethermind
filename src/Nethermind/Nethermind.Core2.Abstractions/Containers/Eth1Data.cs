//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class Eth1Data : IEquatable<Eth1Data>
    {
        public static readonly Eth1Data Zero = new Eth1Data(Root.Zero, 0, Bytes32.Zero);

        public Eth1Data(Root depositRoot, ulong depositCount, Bytes32 blockHash)
        {
            DepositRoot = depositRoot;
            DepositCount = depositCount;
            BlockHash = blockHash;
        }

        public Bytes32 BlockHash { get; }
        
        // TODO: is it ulong? then the tree would not be enough with 32 levels?
        public ulong DepositCount { get; private set; }
        public Root DepositRoot { get; private set; }

        public static Eth1Data Clone(Eth1Data other)
        {
            var clone = new Eth1Data(
                other.DepositRoot,
                other.DepositCount,
                other.BlockHash);
            return clone;
        }

        public static bool operator !=(Eth1Data left, Eth1Data right)
        {
            return !(left == right);
        }

        public static bool operator ==(Eth1Data left, Eth1Data right)
        {
            return EqualityComparer<Eth1Data>.Default.Equals(left, right);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Eth1Data);
        }

        public bool Equals(Eth1Data? other)
        {
            return !(other is null) &&
                BlockHash.Equals(other.BlockHash) &&
                DepositCount == other.DepositCount &&
                DepositRoot.Equals(other.DepositRoot);
        }

        public override int GetHashCode()
        {
            return BlockHash.GetHashCode();
        }

        public void SetDepositCount(ulong depositCount)
        {
            DepositCount = depositCount;
        }

        public void SetDepositRoot(Root depositRoot)
        {
            DepositRoot = depositRoot;
        }

        public override string ToString()
        {
            return $"[dr={DepositRoot.ToString().Substring(0, 10)} dc={DepositCount} bh={BlockHash.ToString().Substring(0, 10)}]";
        }
    }
}
