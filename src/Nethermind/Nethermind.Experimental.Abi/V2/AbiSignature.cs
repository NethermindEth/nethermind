// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core.Crypto;

namespace Nethermind.Experimental.Abi.V2;

// NOTE: For now we support up to 6 arguments in the signature.
public record AbiSignature(
    string Name)
{
    public const int IdLength = 4;

    public AbiSignature<T1> Arg<T1>(IAbi<T1> abi) =>
        new(Name, abi);

    public override string ToString() => $"{Name}()";

    // TODO: Cache this computation
    public byte[] MethodId()
    {
        var asciiBytes = Encoding.ASCII.GetBytes(ToString());
        return Keccak.Compute(asciiBytes).Bytes[..IdLength].ToArray();
    }
}

public sealed record AbiSignature<T1>(
    string Name,
    IAbi<T1> Abi1) : AbiSignature(Name)
{
    public new AbiSignature<T1, T2> Arg<T2>(IAbi<T2> abi) =>
        new(Name, Abi1, abi);

    public override string ToString() => $"{Name}({Abi1})";
}

public sealed record AbiSignature<T1, T2>(
    string Name,
    IAbi<T1> Abi1,
    IAbi<T2> Abi2) : AbiSignature(Name)
{
    public new AbiSignature<T1, T2, T3> Arg<T3>(IAbi<T3> abi) =>
        new(Name, Abi1, Abi2, abi);

    public override string ToString() => $"{Name}({Abi1},{Abi2})";
}

public sealed record AbiSignature<T1, T2, T3>(
    string Name,
    IAbi<T1> Abi1,
    IAbi<T2> Abi2,
    IAbi<T3> Abi3)
    : AbiSignature(Name)
{
    public new AbiSignature<T1, T2, T3, T4> Arg<T4>(IAbi<T4> abi) =>
        new(Name, Abi1, Abi2, Abi3, abi);

    public override string ToString() => $"{Name}({Abi1},{Abi2},{Abi3})";
}

public sealed record AbiSignature<T1, T2, T3, T4>(
    string Name,
    IAbi<T1> Abi1,
    IAbi<T2> Abi2,
    IAbi<T3> Abi3,
    IAbi<T4> Abi4)
{
    public AbiSignature<T1, T2, T3, T4, T5> Arg<T5>(IAbi<T5> abi) =>
        new(Name, Abi1, Abi2, Abi3, Abi4, abi);

    public override string ToString() => $"{Name}({Abi1},{Abi2},{Abi3},{Abi4})";
}

public sealed record AbiSignature<T1, T2, T3, T4, T5>(
    string Name,
    IAbi<T1> Abi1,
    IAbi<T2> Abi2,
    IAbi<T3> Abi3,
    IAbi<T4> Abi4,
    IAbi<T5> Abi5) : AbiSignature(Name)
{
    public new AbiSignature<T1, T2, T3, T4, T5, T6> Arg<T6>(IAbi<T6> abi) =>
        new(Name, Abi1, Abi2, Abi3, Abi4, Abi5, abi);

    public override string ToString() => $"{Name}({Abi1},{Abi2},{Abi3},{Abi4},{Abi5})";
}

public sealed record AbiSignature<T1, T2, T3, T4, T5, T6>(
    string Name,
    IAbi<T1> Abi1,
    IAbi<T2> Abi2,
    IAbi<T3> Abi3,
    IAbi<T4> Abi4,
    IAbi<T5> Abi5,
    IAbi<T6> Abi6) : AbiSignature(Name)
{
    public override string ToString() => $"{Name}({Abi1},{Abi2},{Abi3},{Abi4},{Abi5},{Abi6})";
}
