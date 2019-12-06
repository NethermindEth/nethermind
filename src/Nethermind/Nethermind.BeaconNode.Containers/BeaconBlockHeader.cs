﻿using System;
using System.Collections.Generic;

namespace Cortex.Containers
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
            : this(Slot.Zero, Hash32.Zero, Hash32.Zero, bodyRoot, new BlsSignature())
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
            var clone = new BeaconBlockHeader(Hash32.Clone(other.BodyRoot))
            {
                Slot = other.Slot,
                ParentRoot = Hash32.Clone(other.ParentRoot),
                StateRoot = Hash32.Clone(other.StateRoot),
                BodyRoot = Hash32.Clone(other.BodyRoot),
                Signature = BlsSignature.Clone(other.Signature)
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

        public override bool Equals(object obj)
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
