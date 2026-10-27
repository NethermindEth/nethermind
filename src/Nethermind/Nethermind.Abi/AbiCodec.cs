// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Abi;

/// <summary>Thrown when a value cannot be encoded, or when an input is not the canonical encoding under a signature.</summary>
public class AbiException(Exception? innerException = null) : Exception(null, innerException);

/// <summary>Encodes and decodes call data, return data and event data under typed <see cref="AbiSignature{T}"/>s.</summary>
/// <remarks>See the <see href="https://docs.soliditylang.org/en/latest/abi-spec.html#formal-specification-of-the-encoding">ABI encoding specification</see>.</remarks>
public static class AbiCodec
{
    /// <summary>Encodes <paramref name="arg"/> under <paramref name="signature"/> into a new array: the method ID, if any, followed by the arguments.</summary>
    /// <exception cref="AbiException">
    /// <paramref name="arg"/> cannot be encoded, e.g. a <c>bytesN</c> or <c>T[k]</c> value of the wrong length.
    /// Any other exception raised while encoding becomes the <see cref="Exception.InnerException"/>.
    /// </exception>
    public static byte[] Encode<T>(AbiSignature<T> signature, T arg)
    {
        try
        {
            byte[] buffer = new byte[signature.Size(arg)];
            if (Encode(signature, arg, buffer) != buffer.Length) throw new AbiException();
            return buffer;
        }
        catch (Exception e) when (e is not AbiException)
        {
            throw new AbiException(e);
        }
    }

    /// <summary>Encodes <paramref name="arg"/> under <paramref name="signature"/> into <paramref name="destination"/>.</summary>
    /// <remarks>
    /// Bytes of <paramref name="destination"/> past the encoding are left unchanged, and on failure it may be partly written.
    /// <paramref name="destination"/> must not overlap the memory of <paramref name="arg"/>.
    /// </remarks>
    /// <returns>The number of bytes written, equal to <see cref="AbiSignature{T}.Size(T)"/>.</returns>
    /// <exception cref="AbiException">
    /// <paramref name="destination"/> is shorter than the encoding, or <paramref name="arg"/> cannot be encoded.
    /// Any other exception raised while encoding becomes the <see cref="Exception.InnerException"/>.
    /// </exception>
    public static int Encode<T>(AbiSignature<T> signature, T arg, Span<byte> destination)
    {
        try
        {
            BinarySpanWriter w = new(destination);
            w.Write(signature.MethodId);
            signature.Abi.Write(ref w, arg);
            return w.Written;
        }
        catch (Exception e) when (e is not AbiException)
        {
            throw new AbiException(e);
        }
    }

    /// <summary>Decodes the value whose canonical encoding under <paramref name="signature"/> is exactly <paramref name="source"/>.</summary>
    /// <remarks>
    /// The input must start with the method ID, unless <paramref name="signature"/> is unnamed, and end right after the value.
    /// Codecs from <see cref="AbiType"/> also require:
    /// <list type="bullet">
    /// <item>offsets equal to where the encoder writes each tail, so tails are contiguous and in order;</item>
    /// <item>integers correctly sign- or zero-extended from their declared width;</item>
    /// <item><c>bool</c> values of 0 or 1;</item>
    /// <item><c>address</c> values with the upper 12 bytes zero;</item>
    /// <item>zero padding after <c>bytes</c>, <c>bytesN</c> and <c>string</c> contents;</item>
    /// <item>valid UTF-8 in <c>string</c>;</item>
    /// <item>lengths that fit the input.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="AbiException">
    /// <paramref name="source"/> is not such an encoding. Any other exception raised while decoding becomes the <see cref="Exception.InnerException"/>.
    /// </exception>
    public static T Decode<T>(AbiSignature<T> signature, ReadOnlySpan<byte> source)
    {
        try
        {
            BinarySpanReader r = new(source);

            ReadOnlySpan<byte> id = r.ReadBytes(signature.MethodId.Length);
            if (!Bytes.AreEqual(id, signature.MethodId)) throw new AbiException();

            T arg = signature.Abi.Read(ref r);
            if (r.Remaining != 0) throw new AbiException();

            return arg;
        }
        catch (Exception e) when (e is not AbiException)
        {
            throw new AbiException(e);
        }
    }
}
