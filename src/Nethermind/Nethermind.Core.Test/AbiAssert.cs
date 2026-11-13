// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Contracts;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public static class AbiAssert
{
    /// <summary>
    /// Asserts that <paramref name="function"/> has the given canonical signature, selector and return types.
    /// </summary>
    public static void FunctionMatches<TArgs, TResult>(ContractFunction<TArgs, TResult> function, string signature, string selector, string returns)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(function.Signature.ToString(), Is.EqualTo(signature));
            Assert.That(function.Signature.MethodId.ToHexString(true), Is.EqualTo(selector));
            Assert.That(function.Returns.ToString(), Is.EqualTo(returns));
        }
    }
}
