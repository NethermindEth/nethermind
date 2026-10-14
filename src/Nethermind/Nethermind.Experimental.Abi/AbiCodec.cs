// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Experimental.Abi;

public class AbiException : Exception;

public static class AbiCodec
{
    public static byte[] Encode<T>(AbiSignature<T> signature, T arg)
    {
        int valueSize = signature.Abi.Size(arg);

        byte[] buffer = new byte[AbiSignature.MethodIdLength + valueSize];
        BinarySpanWriter w = new(buffer);

        w.Write(signature.MethodId());
        signature.Abi.Write(ref w, arg);

        if (w.Written != buffer.Length) throw new AbiException();

        return buffer;
    }

    public static T Decode<T>(AbiSignature<T> signature, ReadOnlySpan<byte> source)
    {
        BinarySpanReader r = new(source);

        ReadOnlySpan<byte> id = r.ReadBytes(AbiSignature.MethodIdLength);
        if (!Bytes.AreEqual(id, signature.MethodId())) throw new AbiException();

        T arg = signature.Abi.Read(ref r);

        return arg;
    }
}
