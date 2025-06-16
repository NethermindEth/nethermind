// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core.Crypto;

namespace Nethermind.Experimental.Abi;

public record AbiSignature(string Name)
{
    public const int MethodIdLength = 4;

    // TODO: Come up with a better name for this family of methods
    public AbiSignature<T> With<T>(Abi<T> abi) =>
        new(Name, AbiType.Tuple(abi));

    public AbiSignature<(T1, T2)> With<T1, T2>(Abi<T1> abi1, Abi<T2> abi2) =>
        new(Name, AbiType.Tuple(abi1, abi2));

    public AbiSignature<(T1, T2, T3)> With<T1, T2, T3>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3));

    public AbiSignature<(T1, T2, T3, T4)> With<T1, T2, T3, T4>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4));
}

public record AbiSignature<T>(string Name, Abi<T> Abi)
{
    public override string ToString() => $"{Name}{Abi}";

    // TODO: Cache this computation
    public byte[] MethodId()
    {
        var asciiBytes = Encoding.ASCII.GetBytes(ToString());
        return Keccak.Compute(asciiBytes).Bytes[..AbiSignature.MethodIdLength].ToArray();
    }
}
