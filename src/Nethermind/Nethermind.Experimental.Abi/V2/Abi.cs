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
        signature.Abi1.Write(ref w, arg);

        return buffer.Slice(0, w.Written).ToArray();
    }

    public static byte[] Encode<T1, T2>(AbiSignature<T1, T2> signature, T1 arg1, T2 arg2)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var w = new BinarySpanWriter(buffer);

        w.Write(signature.MethodId());
        signature.Abi1.Write(ref w, arg1);
        signature.Abi2.Write(ref w, arg2);

        return buffer.Slice(0, w.Written).ToArray();
    }

    public static byte[] Encode<T1, T2, T3>(AbiSignature<T1, T2, T3> signature, T1 arg1, T2 arg2, T3 arg3)
    {
        Span<byte> buffer = stackalloc byte[DefaultBufferSize];
        var w = new BinarySpanWriter(buffer);

        w.Write(signature.MethodId());
        signature.Abi1.Write(ref w, arg1);
        signature.Abi2.Write(ref w, arg2);
        signature.Abi3.Write(ref w, arg3);

        return buffer.Slice(0, w.Written).ToArray();
    }

    public static T Decode<T>(AbiSignature<T> signature, byte[] source)
    {
        var r = new BinarySpanReader(source);

        var id = r.ReadBytes(AbiSignature.IdLength);
        if (!Bytes.AreEqual(id, signature.MethodId())) throw new AbiException();

        T arg = signature.Abi1.Read(ref r);

        return arg;
    }

    public static (T1, T2) Decode<T1, T2>(AbiSignature<T1, T2> signature, byte[] source)
    {
        var r = new BinarySpanReader(source);

        var id = r.ReadBytes(AbiSignature.IdLength);
        if (!Bytes.AreEqual(id, signature.MethodId())) throw new AbiException();

        T1 arg1 = signature.Abi1.Read(ref r);
        T2 arg2 = signature.Abi2.Read(ref r);

        return (arg1, arg2);
    }

    public static (T1, T2, T3) Decode<T1, T2, T3>(AbiSignature<T1, T2, T3> signature, byte[] source)
    {
        var r = new BinarySpanReader(source);

        var id = r.ReadBytes(AbiSignature.IdLength);
        if (!Bytes.AreEqual(id, signature.MethodId())) throw new AbiException();

        T1 arg1 = signature.Abi1.Read(ref r);
        T2 arg2 = signature.Abi2.Read(ref r);
        T3 arg3 = signature.Abi3.Read(ref r);

        return (arg1, arg2, arg3);
    }
}
