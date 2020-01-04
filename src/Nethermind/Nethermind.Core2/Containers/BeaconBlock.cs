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

namespace Nethermind.Core2.Containers
{
    public class BeaconBlock
    {
        public const int SszDynamicOffset = Core2.ByteLength.SlotLength + 2 * ByteLength.Hash32Length + sizeof(uint) + BlsSignature.SszLength;
        
        public static int SszLength(BeaconBlock? container)
        {
            return container is null ? 0 : (SszDynamicOffset + BeaconBlockBody.SszLength(container.Body));
        }
        
        public Slot Slot { get; set; }
        public Hash32 ParentRoot { get; set; }
        public Hash32 StateRoot { get; set; }
        public BeaconBlockBody? Body { get; set; }
        public BlsSignature Signature { get; set; } = BlsSignature.Empty;

        public static uint MaxProposerSlashings { get; set; } = 16;

        public static uint MaxAttesterSlashings { get; set; } = 1;

        public static uint MaxAttestations { get; set; } = 128;

        public static uint MaxDeposits { get; set; } = 16;
        
        public static uint MaxVoluntaryExits { get; set; } = 16;
        
        public bool Equals(BeaconBlock other)
        {
            return Slot == other.Slot &&
                   Equals(ParentRoot, other.ParentRoot) &&
                   Equals(StateRoot, other.StateRoot) &&
                   Equals(Body, other.Body) &&
                   Equals(Signature, other.Signature);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((BeaconBlock) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }
    }
}