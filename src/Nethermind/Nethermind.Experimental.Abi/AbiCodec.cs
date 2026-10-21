// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Experimental.Abi;

public class AbiException(Exception? innerException = null) : Exception(null, innerException);

public static class AbiCodec
{
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
