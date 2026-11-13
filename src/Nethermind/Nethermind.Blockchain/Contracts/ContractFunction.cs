// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts;

/// <summary>Contract function: the signature its call data is encoded under and the signature its return data is decoded under.</summary>
/// <typeparam name="TArgs">Type of the function arguments.</typeparam>
/// <typeparam name="TResult">Type of the return values.</typeparam>
public sealed class ContractFunction<TArgs, TResult>(AbiSignature<TArgs> signature, AbiSignature<TResult> returns)
{
    /// <summary>Signature of the call data, including the method ID.</summary>
    public AbiSignature<TArgs> Signature { get; } = signature;

    /// <summary>Unnamed signature of the return data.</summary>
    public AbiSignature<TResult> Returns { get; } = returns;

    public override string ToString() => Signature.ToString();
}

/// <summary>Builds <see cref="ContractFunction{TArgs,TResult}"/> from call signatures.</summary>
public static class ContractFunction
{
    /// <summary>Returns the function with call signature <paramref name="signature"/> and no return values.</summary>
    public static ContractFunction<TArgs, ValueTuple> Returns<TArgs>(this AbiSignature<TArgs> signature) =>
        new(signature, new AbiSignature().With());

    /// <summary>Returns the function with call signature <paramref name="signature"/> and one return value.</summary>
    public static ContractFunction<TArgs, TResult> Returns<TArgs, TResult>(this AbiSignature<TArgs> signature, Abi<TResult> result) =>
        new(signature, new AbiSignature().With(result));

    /// <summary>Returns the function with call signature <paramref name="signature"/> and two return values.</summary>
    public static ContractFunction<TArgs, (T1, T2)> Returns<TArgs, T1, T2>(this AbiSignature<TArgs> signature, Abi<T1> result1, Abi<T2> result2) =>
        new(signature, new AbiSignature().With(result1, result2));
}
