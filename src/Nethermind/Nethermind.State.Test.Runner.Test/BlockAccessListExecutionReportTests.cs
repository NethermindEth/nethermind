// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// The report's lines are read by other tools across clients, so their keys, values and types are pinned exactly.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockAccessListExecutionReportTests
{
    [Test]
    public void Report_writes_one_json_object_per_line_with_the_shared_keys()
    {
        StringWriter output = new();
        BlockAccessListExecutionReport report = new(output);
        Block block = Build.A.Block.WithNumber(12).TestObject;
        string hash = block.Hash!.ToString();
        Assert.That(hash, Does.Match("^0x[0-9a-f]{64}$"));

        report.OnExecutionPathChosen(block, null);
        report.OnExecutionPathChosen(block, "disabled");
        report.OnSequentialRetry(block, new Exception("parallel failed"), null);
        report.OnSequentialRetry(block, new Exception("parallel failed"), new Exception("sequential failed"));

        Assert.That(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(new[]
        {
            $$"""{"event":"balExecution","block":12,"hash":"{{hash}}","path":"parallel","reason":""}""",
            $$"""{"event":"balExecution","block":12,"hash":"{{hash}}","path":"sequential","reason":"disabled"}""",
            $$"""{"event":"balFallback","block":12,"hash":"{{hash}}","parallelError":"parallel failed","sequentialResult":"valid","sequentialError":""}""",
            $$"""{"event":"balFallback","block":12,"hash":"{{hash}}","parallelError":"parallel failed","sequentialResult":"invalid","sequentialError":"sequential failed"}""",
        }));
    }
}
