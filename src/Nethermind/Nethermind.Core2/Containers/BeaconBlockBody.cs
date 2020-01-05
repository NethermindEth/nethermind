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

namespace Nethermind.Core2.Containers
{
    public class BeaconBlockBody
    {
        public bool Equals(BeaconBlockBody other)
        {
            bool basicEquality = Equals(RandaoReversal, other.RandaoReversal) &&
                                 Equals(Eth1Data, other.Eth1Data) &&
                                 Bytes.AreEqual(Graffiti, other.Graffiti) &&
                                 (ProposerSlashings?.Length ?? 0) == (other.ProposerSlashings?.Length ?? 0) &&
                                 (AttesterSlashings?.Length ?? 0) == (other.AttesterSlashings?.Length ?? 0) &&
                                 (Attestations?.Length ?? 0) == (other.Attestations?.Length ?? 0) &&
                                 (Deposits?.Length ?? 0) == (other.Deposits?.Length ?? 0) &&
                                 (VoluntaryExits?.Length ?? 0) == (other.VoluntaryExits?.Length ?? 0);

            if (!basicEquality)
            {
                return false;
            }

            if (!(AttesterSlashings is null) && !(other.AttesterSlashings is null))
            {
                for (int i = 0; i < AttesterSlashings.Length; i++)
                {
                    if (!Equals(AttesterSlashings[i], other.AttesterSlashings[i]))
                    {
                        return false;
                    }
                }
            }

            if (!(ProposerSlashings is null) && !(other.ProposerSlashings is null))
            {
                for (int i = 0; i < ProposerSlashings.Length; i++)
                {
                    if (!Equals(ProposerSlashings[i], other.ProposerSlashings[i]))
                    {
                        return false;
                    }
                }
            }

            if (!(Attestations is null) && !(other.Attestations is null))
            {
                for (int i = 0; i < Attestations.Length; i++)
                {
                    if (!Equals(Attestations[i], other.Attestations[i]))
                    {
                        return false;
                    }
                }
            }

            if (!(Deposits is null) && !(other.Deposits is null))
            {
                for (int i = 0; i < Deposits.Length; i++)
                {
                    if (!Equals(Deposits[i], other.Deposits[i]))
                    {
                        return false;
                    }
                }
            }

            if (!(VoluntaryExits is null) && !(other.VoluntaryExits is null))
            {
                for (int i = 0; i < VoluntaryExits.Length; i++)
                {
                    if (!Equals(VoluntaryExits[i], other.VoluntaryExits[i]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((BeaconBlockBody) obj);
        }

        public override int GetHashCode()
        {
            throw new ArgumentNullException();
        }

        public const int SszDynamicOffset = ByteLength.BlsSignatureLength + ByteLength.Eth1DataLength + 32 + 5 * sizeof(uint);

        public static int SszLength(BeaconBlockBody? container)
        {
            if (container is null)
            {
                return 0;
            }

            int result = SszDynamicOffset;

            result += ByteLength.ProposerSlashingLength * (container.ProposerSlashings?.Length ?? 0);
            result += ByteLength.DepositLength * (container.Deposits?.Length ?? 0);
            result += ByteLength.VoluntaryExitLength * (container.VoluntaryExits?.Length ?? 0);

            result += sizeof(uint) * (container.AttesterSlashings?.Length ?? 0);
            if (!(container.AttesterSlashings is null))
            {
                for (int i = 0; i < container.AttesterSlashings.Length; i++)
                {
                    result += ByteLength.AttesterSlashingLength(container.AttesterSlashings[i]);
                }
            }

            result += sizeof(uint) * (container.Attestations?.Length ?? 0);
            if (!(container.Attestations is null))
            {
                for (int i = 0; i < container.Attestations.Length; i++)
                {
                    result += ByteLength.AttestationLength(container.Attestations[i]);
                }
            }

            return result;
        }

        public BlsSignature RandaoReversal { get; set; } = BlsSignature.Empty;
        public Eth1Data? Eth1Data { get; set; }
        public byte[] Graffiti { get; set; } = new byte[32];
        public ProposerSlashing[]? ProposerSlashings { get; set; }
        public AttesterSlashing[]? AttesterSlashings { get; set; }
        public Attestation?[]? Attestations { get; set; }
        public Deposit?[]? Deposits { get; set; }
        public VoluntaryExit[]? VoluntaryExits { get; set; }
    }
}