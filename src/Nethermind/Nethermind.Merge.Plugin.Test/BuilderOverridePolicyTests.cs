// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Handlers;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class BuilderOverridePolicyTests
{
    [Test]
    public void Composite_should_override_when_any_policy_requests_it()
    {
        Block block = Build.A.Block.TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new CompositeBuilderOverridePolicy().ShouldOverrideBuilder(block), Is.False);
            Assert.That(new CompositeBuilderOverridePolicy(new TestPolicy(false), new TestPolicy(true)).ShouldOverrideBuilder(block), Is.True);
            Assert.That(new CompositeBuilderOverridePolicy(new TestPolicy(false)).ShouldOverrideBuilder(block), Is.False);
        }
    }

    private sealed class TestPolicy(bool result) : IBuilderOverridePolicy
    {
        public bool ShouldOverrideBuilder(Block block) => result;
    }
}
