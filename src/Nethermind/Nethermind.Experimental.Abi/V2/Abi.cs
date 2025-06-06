// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Experimental.Abi.V2;

public class AbiException : Exception;

public static class Abi
{
    private const int DefaultBufferSize = 4096;

    public static byte[] Encode<T>(AbiSignature<T> signature, T arg)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var w = new BinarySpanWriter(buffer);

        w.Write(signature.MethodId());
        signature.Abi.Write(ref w, arg);

        return buffer.Slice(0, w.Written).ToArray();
    }

    public static T Decode<T>(AbiSignature<T> signature, byte[] source)
    {
        var r = new BinarySpanReader(source);

        var id = r.ReadBytes(AbiSignature.MethodIdLength);
        if (!Bytes.AreEqual(id, signature.MethodId())) throw new AbiException();

        T arg = signature.Abi.Read(ref r);

        return arg;
    }
}
