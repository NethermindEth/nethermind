// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class BuilderOverridePolicyTests
{
    [Test]
    public void Should_override_when_any_policy_requests_it()
    {
        Block block = Build.A.Block.TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new TestGetPayloadHandler([]).ShouldOverride(block), Is.False);
            Assert.That(new TestGetPayloadHandler([new TestPolicy(false), new TestPolicy(true)]).ShouldOverride(block), Is.True);
            Assert.That(new TestGetPayloadHandler([new TestPolicy(false)]).ShouldOverride(block), Is.False);
        }
    }

    private sealed class TestGetPayloadHandler(IEnumerable<IBuilderOverridePolicy> policies)
        : GetPayloadHandlerBase<GetPayloadV2Result>(1, null!, null!, LimboLogs.Instance, policies)
    {
        public bool ShouldOverride(Block block) => ShouldOverrideBuilder(block);

        protected override GetPayloadV2Result GetPayloadResultFromBlock(IBlockProductionContext blockProductionContext) =>
            throw new System.NotSupportedException();
    }

    private sealed class TestPolicy(bool result) : IBuilderOverridePolicy
    {
        public bool ShouldOverrideBuilder(Block block) => result;
    }
}
