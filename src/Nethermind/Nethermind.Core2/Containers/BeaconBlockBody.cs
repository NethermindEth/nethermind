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
using Nethermind.Core.Extensions;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Containers
{
    public class BeaconBlockBody
    {
        public bool Equals(BeaconBlockBody other)
        {
            bool basicEquality = Equals(RandaoReversal, other.RandaoReversal) &&
                                 Equals(Eth1Data, other.Eth1Data) &&
                                 Bytes.AreEqual(Graffiti, other.Graffiti) &&
                                 ProposerSlashings.Length == other.ProposerSlashings.Length &&
                                 AttesterSlashings.Length == other.AttesterSlashings.Length &&
                                 Attestations.Length == other.Attestations.Length &&
                                 Deposits.Length == other.Deposits.Length &&
                                 VoluntaryExits.Length == other.VoluntaryExits.Length;

            if (!basicEquality)
            {
                return false;
            }
            
            for (int i = 0; i < AttesterSlashings.Length; i++)
            {
                if (!Equals(AttesterSlashings[i], other.AttesterSlashings[i]))
                {
                    return false;
                }
            }

            for (int i = 0; i < ProposerSlashings.Length; i++)
            {
                if (!Equals(ProposerSlashings[i], other.ProposerSlashings[i]))
                {
                    return false;
                }
            }
            
            for (int i = 0; i < Attestations.Length; i++)
            {
                if (!Equals(Attestations[i], other.Attestations[i]))
                {
                    return false;
                }
            }
            
            for (int i = 0; i < Deposits.Length; i++)
            {
                if (!Equals(Deposits[i], other.Deposits[i]))
                {
                    return false;
                }
            }
            
            for (int i = 0; i < VoluntaryExits.Length; i++)
            {
                if (!Equals(VoluntaryExits[i], other.VoluntaryExits[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((BeaconBlockBody) obj);
        }

        public override int GetHashCode()
        {
            throw new ArgumentNullException();
        }

        public const int SszDynamicOffset = BlsSignature.SszLength + Eth1Data.SszLength + 32 + 5 * sizeof(uint);

        public static int SszLength(BeaconBlockBody container)
        {
            int result = SszDynamicOffset;
            
            result += ProposerSlashing.SszLength * container.ProposerSlashings.Length;
            result += Deposit.SszLength * container.Deposits.Length;
            result += VoluntaryExit.SszLength * container.VoluntaryExits.Length;

            result += sizeof(uint) * container.AttesterSlashings.Length;
            for (int i = 0; i < container.AttesterSlashings.Length; i++)
            {
                result += AttesterSlashing.SszLength(container.AttesterSlashings[i]);
            }

            result += sizeof(uint) * container.Attestations.Length;
            for (int i = 0; i < container.Attestations.Length; i++)
            {
                result += Attestation.SszLength(container.Attestations[i]);
            }

            return result;
        }

        public BlsSignature RandaoReversal { get; set; }
        public Eth1Data Eth1Data { get; set; }
        public byte[] Graffiti { get; set; }
        public ProposerSlashing[] ProposerSlashings { get; set; }
        public AttesterSlashing[] AttesterSlashings { get; set; }
        public Attestation[] Attestations { get; set; }
        public Deposit[] Deposits { get; set; }
        public VoluntaryExit[] VoluntaryExits { get; set; }
    }
}