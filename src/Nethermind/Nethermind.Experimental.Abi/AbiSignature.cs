// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Experimental.Abi;

/// <summary>Function name from which typed signatures are built with <c>With</c>.</summary>
/// <param name="name">
/// <see langword="null"/> for a signature without a method ID, whose encoding and decoding cover only the arguments, as for return data, event data and indexed topics.
/// An empty name still has a method ID, derived from the bare parameter list.
/// </param>
public sealed class AbiSignature(string? name = null)
{
    /// <summary>Length of a method ID, the leading bytes of the Keccak-256 hash of the canonical signature.</summary>
    public const int MethodIdLength = 4;

    /// <summary>The function name, or <see langword="null"/> for a signature without a method ID.</summary>
    public string? Name { get; } = name;

    /// <inheritdoc cref="With{T}(Abi{T})"/>
    public AbiSignature<ValueTuple> With() => new(Name, AbiType.Tuple());

    /// <summary>Returns the signature whose arguments have the given types, e.g. <c>transfer(address,uint256)</c>.</summary>
    /// <remarks>The arguments are encoded as a tuple of the given components.</remarks>
    public AbiSignature<T> With<T>(Abi<T> abi) =>
        new(Name, AbiType.Tuple(abi));

    /// <inheritdoc cref="With{T}(Abi{T})"/>
    public AbiSignature<(T1, T2)> With<T1, T2>(Abi<T1> abi1, Abi<T2> abi2) =>
        new(Name, AbiType.Tuple(abi1, abi2));

    /// <inheritdoc cref="With{T}(Abi{T})"/>
    public AbiSignature<(T1, T2, T3)> With<T1, T2, T3>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3));

    /// <inheritdoc cref="With{T}(Abi{T})"/>
    public AbiSignature<(T1, T2, T3, T4)> With<T1, T2, T3, T4>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4));

    /// <inheritdoc cref="With{T}(Abi{T})"/>
    public AbiSignature<(T1, T2, T3, T4, T5)> With<T1, T2, T3, T4, T5>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4, abi5));

    /// <inheritdoc cref="With{T}(Abi{T})"/>
    public AbiSignature<(T1, T2, T3, T4, T5, T6)> With<T1, T2, T3, T4, T5, T6>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4, abi5, abi6));

    /// <inheritdoc cref="With{T}(Abi{T})"/>
    public AbiSignature<(T1, T2, T3, T4, T5, T6, T7)> With<T1, T2, T3, T4, T5, T6, T7>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6, Abi<T7> abi7) =>
        new(Name, AbiType.Tuple(abi1, abi2, abi3, abi4, abi5, abi6, abi7));
}

/// <summary>Function signature whose arguments are a value of type <typeparamref name="T"/>, built with <see cref="AbiSignature"/>.</summary>
public sealed class AbiSignature<T>
{
    internal AbiSignature(string? name, Abi<T> abi)
    {
        Name = name;
        Abi = abi;
        Hash = Keccak.Compute(ToString());
    }

    /// <summary>The function name, or <see langword="null"/> for a signature without a method ID.</summary>
    public string? Name { get; }
    /// <summary>Codec of the argument tuple.</summary>
    public Abi<T> Abi { get; }
    /// <summary>Keccak-256 hash of the canonical signature; for events, the first topic.</summary>
    public Hash256 Hash { get; }

    /// <summary>First <see cref="AbiSignature.MethodIdLength"/> bytes of <see cref="Hash"/>, or empty when <see cref="Name"/> is <see langword="null"/>.</summary>
    public ReadOnlySpan<byte> MethodId => Name is null ? [] : Hash.Bytes[..AbiSignature.MethodIdLength];

    /// <summary>Returns the length of the encoding of <paramref name="value"/>, including the method ID.</summary>
    public int Size(T value) => MethodId.Length + Abi.Size(value);

    /// <summary>Returns the canonical signature, e.g. <c>transfer(address,uint256)</c>.</summary>
    public override string ToString() => $"{Name}{Abi}";
}
