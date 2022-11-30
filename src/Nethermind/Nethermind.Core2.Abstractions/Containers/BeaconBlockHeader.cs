// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class BeaconBlockHeader : IEquatable<BeaconBlockHeader>
    {
        public static readonly BeaconBlockHeader Zero =
            new BeaconBlockHeader(Slot.Zero, Root.Zero, Root.Zero, Root.Zero);

        public BeaconBlockHeader(
            Slot slot,
            Root parentRoot,
            Root stateRoot,
            Root bodyRoot)
        {
            Slot = slot;
            ParentRoot = parentRoot;
            StateRoot = stateRoot;
            BodyRoot = bodyRoot;
        }

        public BeaconBlockHeader(Root bodyRoot)
            : this(Slot.Zero, Root.Zero, Root.Zero, bodyRoot)
        {
        }

        public Root BodyRoot { get; private set; }
        public Root ParentRoot { get; private set; }
        public Slot Slot { get; private set; }
        public Root StateRoot { get; private set; }

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
                BodyRoot = other.BodyRoot
            };
            return clone;
        }

        public void SetStateRoot(Root stateRoot)
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

            return BodyRoot.Equals(other.BodyRoot)
                && ParentRoot.Equals(other.ParentRoot)
                && Slot == other.Slot
                && StateRoot.Equals(other.StateRoot);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(BodyRoot, ParentRoot, Slot, StateRoot);
        }

        public override bool Equals(object? obj)
        {
            var other = obj as BeaconBlockHeader;
            return !(other is null) && Equals(other);
        }

        public override string ToString()
        {
            return $"s={Slot}_p={ParentRoot.ToString().Substring(0, 10)}_st={StateRoot.ToString().Substring(0, 10)}_bd={BodyRoot.ToString().Substring(0, 10)}";
        }
    }
}
