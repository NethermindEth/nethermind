// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class Deposit
    {
        private readonly List<Bytes32> _proof;

        public Deposit(IEnumerable<Bytes32> proof, Ref<DepositData> data)
        {
            _proof = new List<Bytes32>(proof);
            Data = data;
        }

        public Ref<DepositData> Data { get; }

        [DebuggerHidden]
        public IReadOnlyList<Bytes32> Proof => _proof.AsReadOnly();

        public override string ToString()
        {
            return $"I:{Proof[^1].ToString().Substring(0, 12)} P:{Data.Item.PublicKey.ToString().Substring(0, 12)} A:{Data.Item.Amount}";
        }

        public bool Equals(Deposit other)
        {
            return Data.Equals(other.Data)
                && Proof.SequenceEqual(other.Proof);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is Deposit other && Equals(other);
        }

        public override int GetHashCode()
        {
            HashCode hashCode = new HashCode();
            foreach (Bytes32 value in Proof)
            {
                hashCode.Add(value);
            }
            hashCode.Add(Data);
            return hashCode.ToHashCode();
        }
    }
}
