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
using Nethermind.Core2.Containers;
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
            return new CommitteeIndex(DecodeUInt(span));
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
        
        public static Gwei DecodeGwei(Span<byte> span)
        {
            return new Gwei(DecodeULong(span));
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
            return new ValidatorIndex(DecodeUInt(span));
        }
    }
}