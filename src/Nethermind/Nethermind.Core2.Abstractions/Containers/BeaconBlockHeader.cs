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
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
//using System.Reflection.Metadata.Ecma335;
using Hash32 = Nethermind.Core2.Crypto.Hash32;

namespace Nethermind.Core2.Containers
{
    public class BeaconBlockHeader : IEquatable<BeaconBlockHeader>
    {
        public BeaconBlockHeader(
            Slot slot,
            Hash32 parentRoot,
            Hash32 stateRoot,
            Hash32 bodyRoot,
            BlsSignature signature)
        {
            Slot = slot;
            ParentRoot = parentRoot;
            StateRoot = stateRoot;
            BodyRoot = bodyRoot;
            Signature = signature;
        }

        public BeaconBlockHeader(Hash32 bodyRoot)
            : this(Slot.Zero, Hash32.Zero, Hash32.Zero, bodyRoot, BlsSignature.Empty)
        {
        }

        public Hash32 BodyRoot { get; private set; }
        public Hash32 ParentRoot { get; private set; }
        public BlsSignature Signature { get; private set; }
        public Slot Slot { get; private set; }
        public Hash32 StateRoot { get; private set; }

        /// <summary>
        /// Creates a deep copy of the object.
        /// </summary>
        public static BeaconBlockHeader Clone(BeaconBlockHeader other)
        {
            var clone = new BeaconBlockHeader(other.BodyRoot)
            {
                Slot = other.Slot,
                ParentRoot = other.ParentRoot,
                StateRoot = other.StateRoot,
                BodyRoot = other.BodyRoot,
                Signature = new BlsSignature(other.Signature.Bytes)
            };
            return clone;
        }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }

        public void SetStateRoot(Hash32 stateRoot)
        {
            StateRoot = stateRoot;
        }

        public bool Equals(BeaconBlockHeader other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            if (ReferenceEquals(other, this))
            {
                return true;
            }
            
            return BodyRoot == other.BodyRoot
                && ParentRoot == other.ParentRoot
                && Signature == other.Signature
                && Slot == other.Slot
                && StateRoot == other.StateRoot;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(BodyRoot, ParentRoot, Signature, Slot, StateRoot);
        }

        public override bool Equals(object? obj)
        {
            var other = obj as BeaconBlockHeader;
            return !(other is null) && Equals(other);
        }

        public override string ToString()
        {
            return $"S:{Slot} P:{ParentRoot.ToString().Substring(0, 12)} St:{StateRoot.ToString().Substring(0, 12)} Bd:{BodyRoot.ToString().Substring(0, 12)}";
        }
    }
}
