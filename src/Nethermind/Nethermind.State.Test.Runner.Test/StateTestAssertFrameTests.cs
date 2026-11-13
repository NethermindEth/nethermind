// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

public class StateTestAssertFrameTests
{
    [Test]
    public async Task Assert_frame_reads_the_transaction_diff()
    {
        string file = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "assert_frame_success_commits_body.json");
        List<GeneralStateTest> tests = [.. new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), file).LoadTests<GeneralStateTest>()];
        Assert.That(tests, Has.Count.EqualTo(1));
        Assert.That(tests[0].LoadFailure, Is.Null);

        StateTestsRunner runner = new(WhenTrace.Never, traceMemory: false, traceStack: false, chainId: BlockchainIds.Mainnet, suppressOutput: true);
        EthereumTestResult result = await runner.RunSingleTestAsync(tests[0]);

        Assert.That(result.Pass, Is.True, result.Error);
    }
}
