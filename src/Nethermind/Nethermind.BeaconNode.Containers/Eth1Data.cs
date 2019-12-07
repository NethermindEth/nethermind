using System;
using System.Collections.Generic;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class Eth1Data : IEquatable<Eth1Data>
    {
        public Eth1Data(ulong depositCount, Hash32 eth1BlockHash)
            : this(Hash32.Zero, depositCount, eth1BlockHash)
        {
        }

        public Eth1Data(Hash32 depositRoot, ulong depositCount, Hash32 blockHash)
        {
            DepositRoot = depositRoot;
            DepositCount = depositCount;
            BlockHash = blockHash;
        }

        public Hash32 BlockHash { get; }
        public ulong DepositCount { get; private set; }
        public Hash32 DepositRoot { get; private set; }

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
                BlockHash == other.BlockHash &&
                DepositCount == other.DepositCount &&
                DepositRoot.Equals(other.DepositRoot);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(BlockHash, DepositCount, DepositRoot);
        }

        public void SetDepositCount(ulong depositCount)
        {
            DepositCount = depositCount;
        }

        public void SetDepositRoot(Hash32 depositRoot)
        {
            if (depositRoot == null)
            {
                throw new ArgumentNullException(nameof(depositRoot));
            }
            DepositRoot = depositRoot;
        }
    }
}
