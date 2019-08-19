/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.IO;

namespace Nethermind.Core.Encoding
{
    public interface IRlpDecoder
    {
    }
    
    public interface IRlpDecoder<T> : IRlpDecoder
    {
        T Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
        Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
        void Encode(MemoryStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
        int GetLength(T item, RlpBehaviors rlpBehaviors);
    }
    
    public interface IRlpValueDecoder<T> : IRlpDecoder
    {
        T Decode(Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
        Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
        void Encode(MemoryStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
        int GetLength(T item, RlpBehaviors rlpBehaviors);
    }
}