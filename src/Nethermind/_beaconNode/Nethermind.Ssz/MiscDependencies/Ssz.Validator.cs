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
using System.Runtime.CompilerServices;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int ValidatorLength = Ssz.BlsPublicKeyLength + Ssz.Bytes32Length + Ssz.GweiLength + sizeof(bool) + 4 * Ssz.EpochLength;

        public static void Encode(Span<byte> span, Validator container)
        {
            if (span.Length != Ssz.ValidatorLength) ThrowTargetLength<Validator>(span.Length, Ssz.ValidatorLength);
            if (container == null) return;
            int offset = 0;
            Encode(span, container.PublicKey, ref offset);
            Encode(span, container.WithdrawalCredentials, ref offset);
            Encode(span, container.EffectiveBalance, ref offset);
            Encode(span, container.IsSlashed, ref offset);
            Encode(span, container.ActivationEligibilityEpoch, ref offset);
            Encode(span, container.ActivationEpoch, ref offset);
            Encode(span, container.ExitEpoch, ref offset);
            Encode(span.Slice(offset), container.WithdrawableEpoch);
        }

        public static Validator DecodeValidator(Span<byte> span)
        {
            if (span.Length != Ssz.ValidatorLength) ThrowSourceLength<Validator>(span.Length, Ssz.ValidatorLength);
            int offset = 0;
            BlsPublicKey publicKey = DecodeBlsPublicKey(span, ref offset);
            Bytes32 withdrawalCredentials = DecodeBytes32(span, ref offset);
            Gwei effectiveBalance = DecodeGwei(span, ref offset);
            bool isSlashed = DecodeBool(span, ref offset);
            Epoch activationEligibilityEpoch = DecodeEpoch(span, ref offset);
            Epoch activationEpoch = DecodeEpoch(span, ref offset);
            Epoch exitEpoch = DecodeEpoch(span, ref offset);
            Epoch withdrawableEpoch = DecodeEpoch(span, ref offset);
            Validator container = new Validator(publicKey, withdrawalCredentials, effectiveBalance, isSlashed,
                activationEligibilityEpoch, activationEpoch, exitEpoch, withdrawableEpoch);
            return container;
        }

        public static void Encode(Span<byte> span, Validator[]? containers)
        {
            if (containers is null)
            {
                return;
            }
            
            if (span.Length != Ssz.ValidatorLength * containers.Length)
            {
                ThrowTargetLength<Validator>(span.Length, Ssz.ValidatorLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * Ssz.ValidatorLength, Ssz.ValidatorLength), containers[i]);
            }
        }
        
        public static Validator[] DecodeValidators(Span<byte> span)
        {
            if (span.Length % Ssz.ValidatorLength != 0)
            {
                ThrowInvalidSourceArrayLength<Validator>(span.Length, Ssz.ValidatorLength);
            }

            int count = span.Length / Ssz.ValidatorLength;
            Validator[] containers = new Validator[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeValidator(span.Slice(i * Ssz.ValidatorLength, Ssz.ValidatorLength));
            }

            return containers;
        }
    }
}