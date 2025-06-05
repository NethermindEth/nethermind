// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Experimental.Abi.V2;

public class AbiException : Exception;

public static class Abi
{
    public static byte[] Encode<T>(AbiSignature<T> signature, T arg)
    {
        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer);

        w.Write(signature.MethodId());
        signature.Abi1.Write(w, arg);

        return buffer.ToArray();
    }

    public static byte[] Encode<T1, T2>(AbiSignature<T1, T2> signature, T1 arg1, T2 arg2)
    {
        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer);

        w.Write(signature.MethodId());
        signature.Abi1.Write(w, arg1);
        signature.Abi2.Write(w, arg2);

        return buffer.ToArray();
    }

    public static T Decode<T>(AbiSignature<T> signature, byte[] source)
    {
        using var r = new BinaryReader(new MemoryStream(source));

        var id = r.ReadBytes(AbiSignature.IdLength);
        if (!Bytes.AreEqual(id, signature.MethodId())) throw new AbiException();

        T arg = signature.Abi1.Read(r);

        return arg;
    }

    public static (T1, T2) Decode<T1, T2>(AbiSignature<T1, T2> signature, byte[] source)
    {
        using var r = new BinaryReader(new MemoryStream(source));

        var id = r.ReadBytes(AbiSignature.IdLength);
        if (!Bytes.AreEqual(id, signature.MethodId())) throw new AbiException();

        T1 arg1 = signature.Abi1.Read(r);
        T2 arg2 = signature.Abi2.Read(r);

        return (arg1, arg2);
    }
}
