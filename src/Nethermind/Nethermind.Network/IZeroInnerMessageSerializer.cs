// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    /// <summary>A message serializer that measures a message before writing it, so the message can be sized or wrapped.</summary>
    /// <remarks>
    /// Follows the <see cref="IRlpDecoder{T}"/> encode contract: <see cref="PopulateLength"/> begins a container for each length
    /// <see cref="Serialize(IByteBuffer, T, ref LengthList)"/> reads, in the order it reads them, and adds
    /// the total serialized length to the writer it is given, so every length is measured once.
    /// </remarks>
    public interface IZeroInnerMessageSerializer<T> : IZeroMessageSerializer<T> where T : MessageBase
    {
        void PopulateLength(T message, in LengthListWriter lengths);

        void Serialize(IByteBuffer byteBuffer, T message, ref LengthList lengths);
    }
}
