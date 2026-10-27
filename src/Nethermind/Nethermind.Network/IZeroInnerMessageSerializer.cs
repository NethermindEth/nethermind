// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    /// <summary>A message serializer that measures a message before writing it, so the message can be sized or wrapped.</summary>
    /// <remarks>
    /// Follows the <see cref="IRlpDecoder{T}"/> encode contract: <see cref="PopulateLength"/> pushes the lengths
    /// <see cref="Serialize(IByteBuffer, T, ref LengthStack)"/> needs in the reverse of the order it pops them, and returns
    /// the total serialized length, so every length is measured once.
    /// </remarks>
    public interface IZeroInnerMessageSerializer<T> : IZeroMessageSerializer<T> where T : MessageBase
    {
        int PopulateLength(T message, ref LengthStack lengths);

        void Serialize(IByteBuffer byteBuffer, T message, ref LengthStack lengths);
    }
}
