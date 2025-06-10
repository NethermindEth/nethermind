// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Extensions;

namespace Nethermind.Experimental.Abi.V2;

public class AbiException : Exception;

public static class Abi
{
    public static byte[] Encode<T>(AbiSignature<T> signature, T arg)
    {
        var valueSize = signature.Abi.Size(arg);

        byte[] buffer = new byte[AbiSignature.MethodIdLength + valueSize];
        var w = new BinarySpanWriter(buffer);

        w.Write(signature.MethodId());
        signature.Abi.Write(ref w, arg);

        Debug.Assert(w.Written == buffer.Length, "Abi encoding did not write the expected number of bytes");

        return buffer;
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
