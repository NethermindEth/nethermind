// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Arkiv.Rpc;
using NUnit.Framework;

namespace Nethermind.Arkiv.Test;

public class ArkivRpcModuleTests
{
    // arkiv-reth takes the block as a JSON number; the eth_* habit is a hex quantity. Both must work.
    // What Nethermind's binder hands a string parameter: a JSON number's raw text, or a JSON string's value.
    [TestCase("739018", 739018UL)]
    [TestCase("0xb46ca", 739018UL)]
    [TestCase(null, null)]
    [TestCase("latest", null)]
    public void Block_argument_takes_numbers_and_quantities(string? block, ulong? expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ArkivRpcModule.TryBlock(block, out ulong? number), Is.True);
            Assert.That(number, Is.EqualTo(expected));
        }
    }

    [TestCase("pending")]
    [TestCase("-1")]
    [TestCase("0xzz")]
    public void Block_argument_refuses_anything_else(string block) =>
        Assert.That(ArkivRpcModule.TryBlock(block, out _), Is.False);
}
