// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
