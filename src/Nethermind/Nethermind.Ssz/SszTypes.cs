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
using System.IO;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public partial class Ssz
    {
        public static void Encode(Span<byte> span, CommitteeIndex value)
        {
            Encode(span, value.Number);
        }
        
        public static CommitteeIndex DecodeCommitteeIndex(Span<byte> span)
        {
            return new CommitteeIndex(DecodeULong(span));
        }

        public static void Encode(Span<byte> span, Epoch value)
        {
            Encode(span, value.Number);
        }
        
        public static Epoch DecodeEpoch(Span<byte> span)
        {
            return new Epoch(DecodeULong(span));
        }
        
        public static void Encode(Span<byte> span, ForkVersion value)
        {
            Encode(span, value.Number);
        }
        
        public static ForkVersion DecodeForkVersion(Span<byte> span)
        {
            return new ForkVersion(DecodeUInt(span));
        }
        
        public static void Encode(Span<byte> span, Gwei value)
        {
            Encode(span, value.Amount);
        }
        
        public static void Encode(Span<byte> span, Gwei[] value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                Encode(span.Slice(i * Gwei.SszLength, Gwei.SszLength), value[i].Amount);    
            }
        }
        
        public static Gwei DecodeGwei(Span<byte> span)
        {
            return new Gwei(DecodeULong(span));
        }
        
        public static void Encode(Span<byte> span, BlsPublicKey value)
        {
            Encode(span, value.Bytes);
        }
        
        public static BlsPublicKey DecodeBlsPublicKey(Span<byte> span)
        {
            return new BlsPublicKey(DecodeBytes(span).ToArray());
        }    
        
        public static void Encode(Span<byte> span, BlsSignature value)
        {
            Encode(span, value.Bytes);
        }
        
        public static BlsSignature DecodeBlsSignature(Span<byte> span)
        {
            return new BlsSignature(DecodeBytes(span).ToArray());
        }    
        
        public static void Encode(Span<byte> span, Sha256 value)
        {
            Encode(span, value?.Bytes ?? Sha256.Zero.Bytes);
        }
        
        public static void Encode(Span<byte> span, Span<Sha256> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                Encode(span.Slice(i * Sha256.SszLength, Sha256.SszLength), value[i]);    
            }
        }
       
        public static Sha256 DecodeSha256(Span<byte> span)
        {
            return Bytes.AreEqual(Bytes.Zero32, span) ? null : new Sha256(DecodeBytes(span).ToArray());
        }
        
        public static Sha256[] DecodeHashes(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<Sha256>();
            }
            
            int count = span.Length / Sha256.SszLength;
            Sha256[] result = new Sha256[count];
            for (int i = 0; i < count; i++)
            {
                Span<byte> current = span.Slice(i * Sha256.SszLength, Sha256.SszLength);
                result[i] = DecodeSha256(current);
            }

            return result;
        }

        public static void Encode(Span<byte> span, Slot value)
        {
            Encode(span, value.Number);
        }
        
        public static Slot DecodeSlot(Span<byte> span)
        {
            return new Slot(DecodeULong(span));
        }    
        
        public static void Encode(Span<byte> span, ValidatorIndex value)
        {
            Encode(span, value.Number);
        }

        public static ValidatorIndex DecodeValidatorIndex(Span<byte> span)
        {
            return new ValidatorIndex(DecodeULong(span));
        }
        
        public static void Encode(Span<byte> span, Span<ValidatorIndex> value)
        {
            Encode(span, MemoryMarshal.Cast<ValidatorIndex, ulong>(value));
        }
        
        public static ValidatorIndex[] DecodeValidatorIndexes(Span<byte> span)
        {
            return MemoryMarshal.Cast<byte, ValidatorIndex>(span).ToArray();
        }
    }
}