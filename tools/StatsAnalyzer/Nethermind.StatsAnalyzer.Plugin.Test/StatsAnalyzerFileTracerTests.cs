// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.StatsAnalyzer.Plugin.Tracer;
using Nethermind.StatsAnalyzer.Plugin.Types;
using NUnit.Framework;
using Testably.Abstractions.Testing;

namespace Nethermind.StatsAnalyzer.Plugin.Test;

[TestFixture]
public class StatsAnalyzerFileTracerTests
{
    private const string ReportFile = "contract-report.json";

    [Test]
    public void Tracer_with_only_the_public_contract_writes_on_due_blocks()
    {
        MockFileSystem fileSystem = new();
        fileSystem.Initialize();
        ContractFileTracer tracer = new(fileSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() =>
            {
                for (int number = 1; number <= 2; number++)
                {
                    tracer.StartNewBlockTrace(Build.A.Block.WithNumber(number).TestObject);
                    tracer.EndBlockTrace();
                    tracer.CompleteAllTasks();
                }
            }, Throws.Nothing);
            Assert.That(fileSystem.File.ReadAllText(ReportFile), Is.EqualTo("\"1-2\""));
        }
    }

    private sealed class ContractTxTracer : TxTracer, IStatsAnalyzerTxTracer<string>
    {
        public string BuildResult(ulong fromBlock = 0UL, ulong toBlock = 0UL) => $"{fromBlock}-{toBlock}";

        public void SetSkip(bool skip) { }
    }

    private sealed class ContractFileTracer(MockFileSystem fileSystem)
        : StatsAnalyzerFileTracer<string, ContractTxTracer>(new ContractTxTracer(), ReportFile, 1, fileSystem,
            LimboLogs.Instance.GetClassLogger<ContractFileTracer>(), 2, ProcessingMode.Sequential, SortOrder.Descending,
            CancellationToken.None, ReportFile, new BlocksConfig())
    {
        protected override void ResetBufferAndTracer() => Tracer = new ContractTxTracer();
    }
}
