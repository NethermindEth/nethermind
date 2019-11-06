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
using Nethermind.Core.Crypto;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public partial class Ssz
    {
        public static void Encode(Span<byte> span, Fork container)
        {
            if (span.Length != Fork.SszLength)
            {
                ThrowInvalidTargetLength<Fork>(span.Length, Fork.SszLength);
            }

            Encode(span.Slice(0, ForkVersion.SszLength), container.PreviousVersion);
            Encode(span.Slice(ForkVersion.SszLength, ForkVersion.SszLength), container.CurrentVersion);
            Encode(span.Slice(2 * ForkVersion.SszLength), container.Epoch);
        }
        
        public static Fork DecodeFork(Span<byte> span)
        {
            if (span.Length != Fork.SszLength)
            {
                ThrowInvalidSourceLength<Fork>(span.Length, Fork.SszLength);
            }

            ForkVersion previous = DecodeForkVersion(span.Slice(0, ForkVersion.SszLength));
            ForkVersion current = DecodeForkVersion(span.Slice(ForkVersion.SszLength, ForkVersion.SszLength));
            Epoch epoch = DecodeEpoch(span.Slice(2 * ForkVersion.SszLength));
            
            return new Fork(previous, current, epoch);
        }
        
        public static void Encode(Span<byte> span, Checkpoint container)
        {
            if (span.Length != Checkpoint.SszLength)
            {
                ThrowInvalidTargetLength<Checkpoint>(span.Length, Checkpoint.SszLength);
            }

            Encode(span.Slice(0, Epoch.SszLength), container.Epoch);
            Encode(span.Slice(Epoch.SszLength), container.Root);
        }
        
        public static Checkpoint DecodeCheckpoint(Span<byte> span)
        {
            if (span.Length != Checkpoint.SszLength)
            {
                ThrowInvalidSourceLength<Checkpoint>(span.Length, Checkpoint.SszLength);
            }
            
            Epoch epoch = DecodeEpoch(span.Slice(0, Epoch.SszLength));
            Sha256 root = DecodeSha256(span.Slice(Epoch.SszLength));
            
            return new Checkpoint(epoch, root);
        }
        
        public static void Encode(Span<byte> span, Validator container)
        {
            if (span.Length != Validator.SszLength)
            {
                ThrowInvalidTargetLength<Validator>(span.Length, Validator.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, BlsPublicKey.SszLength), container.PublicKey);
            offset += BlsPublicKey.SszLength;
            Encode(span.Slice(offset, Sha256.SszLength), container.WithdrawalCredentials);
            offset += Sha256.SszLength;
            Encode(span.Slice(offset, Gwei.SszLength), container.EffectiveBalance);
            offset += Gwei.SszLength;
            Encode(span.Slice(offset, 1), container.Slashed);
            offset += 1;
            Encode(span.Slice(offset, Epoch.SszLength), container.ActivationEligibilityEpoch);
            offset += Epoch.SszLength;
            Encode(span.Slice(offset, Epoch.SszLength), container.ActivationEpoch);
            offset += Epoch.SszLength;
            Encode(span.Slice(offset, Epoch.SszLength), container.ExitEpoch);
            offset += Epoch.SszLength;
            Encode(span.Slice(offset), container.WithdrawableEpoch);
        }
        
        public static Validator DecodeValidator(Span<byte> span)
        {
            if (span.Length != Validator.SszLength)
            {
                ThrowInvalidSourceLength<Validator>(span.Length, Validator.SszLength);
            }
            
            int offset = 0;
            BlsPublicKey publicKey = DecodeBlsPublicKey(span.Slice(offset, BlsPublicKey.SszLength));
            Validator container = new Validator(publicKey);
            offset += BlsPublicKey.SszLength;
            container.WithdrawalCredentials = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            container.EffectiveBalance = DecodeGwei(span.Slice(offset, Gwei.SszLength));
            offset += Gwei.SszLength;
            container.Slashed = DecodeBool(span.Slice(offset, 1));
            offset += 1;
            container.ActivationEligibilityEpoch = DecodeEpoch(span.Slice(offset, Epoch.SszLength));
            offset += Epoch.SszLength;
            container.ActivationEpoch = DecodeEpoch(span.Slice(offset, Epoch.SszLength));
            offset += Epoch.SszLength;
            container.ExitEpoch = DecodeEpoch(span.Slice(offset, Epoch.SszLength));
            offset += Epoch.SszLength;
            container.WithdrawableEpoch = DecodeEpoch(span.Slice(offset));

            return container;
        }
        
         public static void Encode(Span<byte> span, AttestationData container)
        {
            if (span.Length != AttestationData.SszLength)
            {
                ThrowInvalidTargetLength<AttestationData>(span.Length, AttestationData.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, Slot.SszLength), container.Slot);
            offset += Slot.SszLength;
            Encode(span.Slice(offset, CommitteeIndex.SszLength), container.CommitteeIndex);
            offset += CommitteeIndex.SszLength;
            Encode(span.Slice(offset, Sha256.SszLength), container.BeaconBlockRoot);
            offset += Sha256.SszLength;
            Encode(span.Slice(offset, Checkpoint.SszLength), container.Source);
            offset += Checkpoint.SszLength;
            Encode(span.Slice(offset, Checkpoint.SszLength), container.Target);
        }
        
        public static AttestationData DecodeAttestationData(Span<byte> span)
        {
            if (span.Length != AttestationData.SszLength)
            {
                ThrowInvalidSourceLength<AttestationData>(span.Length, AttestationData.SszLength);
            }
            
            
            AttestationData container = new AttestationData();
            int offset = 0;
            container.Slot = DecodeSlot(span.Slice(offset, Slot.SszLength));
            offset += Slot.SszLength;
            container.CommitteeIndex = DecodeCommitteeIndex(span.Slice(offset, CommitteeIndex.SszLength));
            offset += CommitteeIndex.SszLength;
            container.BeaconBlockRoot = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            container.Source = DecodeCheckpoint(span.Slice(offset, Checkpoint.SszLength));
            offset += Checkpoint.SszLength;
            container.Target = DecodeCheckpoint(span.Slice(offset, Checkpoint.SszLength));
            return container;
        }
    }
}