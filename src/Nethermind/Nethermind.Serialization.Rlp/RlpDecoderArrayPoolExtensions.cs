// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Provides pool-backed RLP encoding helpers for decoders.
/// </summary>
public static class RlpDecoderArrayPoolExtensions
{
    /// <summary>
    /// Encodes <paramref name="item"/> into a pool-rented <see cref="ArrayPoolList{T}"/> of bytes, producing
    /// the same bytes as <see cref="Rlp.Encode{T}"/> without the intermediate allocation. Ownership transfers
    /// to the caller, which MUST dispose the result.
    /// </summary>
    public static ArrayPoolList<byte> EncodeToArrayPoolList<T>(this IRlpDecoder<T> decoder, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoder.GetLength(item, rlpBehaviors);
        ArrayPoolList<byte> buffer = new(length, length);
        try
        {
            RlpStream stream = new(new CappedArray<byte>(buffer.UnsafeGetInternalArray(), length));
            decoder.Encode(stream, item, rlpBehaviors);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
