// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Experimental.Abi;

public sealed class AbiSignature(string? name = null)
{
    public const int MethodIdLength = 4;

    public string? Name { get; } = name;

    // TODO: Come up with a better name for this family of methods
    public AbiSignature<ValueTuple> With() => new(Name, AbiType.Tuple());

    public AbiSignature<T> With<T>(Abi<T> abi) =>
        new(Name, AbiType.Tuple(abi));

    public AbiSignature<(T1, T2)> With<T1, T2>(Abi<T1> abi1, Abi<T2> abi2) =>
        new(Name, AbiType.Tuple(abi1, abi2));

    public AbiSignature<(T1, T2, T3)> With<T1, T2, T3>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3));

    public AbiSignature<(T1, T2, T3, T4)> With<T1, T2, T3, T4>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4));

    public AbiSignature<(T1, T2, T3, T4, T5)> With<T1, T2, T3, T4, T5>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4, abi5));

    public AbiSignature<(T1, T2, T3, T4, T5, T6)> With<T1, T2, T3, T4, T5, T6>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4, abi5, abi6));

    public AbiSignature<(T1, T2, T3, T4, T5, T6, T7)> With<T1, T2, T3, T4, T5, T6, T7>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6, Abi<T7> abi7) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4, abi5, abi6, abi7));
}

public sealed class AbiSignature<T>
{
    internal AbiSignature(string? name, Abi<T> abi)
    {
        Name = name;
        Abi = abi;
        Hash = Keccak.Compute(ToString());
    }

    public string? Name { get; }
    public Abi<T> Abi { get; }
    public Hash256 Hash { get; }

    public ReadOnlySpan<byte> MethodId => Name is null ? [] : Hash.Bytes[..AbiSignature.MethodIdLength];

    public int Size(T value) => MethodId.Length + Abi.Size(value);

    public override string ToString() => $"{Name}{Abi}";
}
