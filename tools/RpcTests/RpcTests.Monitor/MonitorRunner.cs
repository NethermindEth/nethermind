// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks.Dataflow;
using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal class MonitorRunner(ExecutionArgs args, INotifier notifier, IStatsReporter stats, HttpClient client, ReorgTracker reorgTracker)
{
    private static readonly TimeSpan ReorgsPeriodOnFail = TimeSpan.FromMinutes(15);

    private readonly TestDefinition[] _tests = TestLoader.Load(args.TestGlobs, requiresResponse: args.ReferenceUrl is null);
    private readonly TestExecutor _executor = new(stats, client);
    private readonly ErrorReporter _errorReporter = new(notifier, stats);

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"Loaded {_tests.Length} tests");
        if (_tests.Length == 0)
            return;

        (ITargetBlock<BlockInfo> startBlock, IDataflowBlock endBlock) = BuildPipeline(ct);

        HeadMonitor headMonitor = new(args.TargetUrl, notifier, _errorReporter);
        try
        {
            Console.WriteLine($"Monitoring {args.TargetUrl}");
            await foreach (BlockInfo head in headMonitor.SubscribeAsync(ct))
            {
                stats.RecordHeadUpdate();
                Console.WriteLine($"New head: {head}");

                if (reorgTracker.OnNewHead(head) is { } reorg)
                {
                    stats.RecordReorg();
                    Console.WriteLine($"Reorg detected: {reorg}");
                }

                if (!startBlock.Post(head))
                    Console.Error.WriteLine($"Head #{head:#} skipped — pipeline busy");
            }
        }
        catch (OperationCanceledException) { }

        startBlock.Complete();
        await endBlock.Completion;
    }

    private (ITargetBlock<BlockInfo> start, IDataflowBlock end) BuildPipeline(CancellationToken ct)
    {
        BufferBlock<BlockInfo> headBuffer = new(new DataflowBlockOptions { BoundedCapacity = 2 });

        DataflowLinkOptions propagate = new() { PropagateCompletion = true };

        // filter tests that need to run for this head
        TransformManyBlock<BlockInfo, TestContext> filterBlock = new(
            FilterTests,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = _tests.Length }
        );

        // run each triggered test against both nodes, yield on mismatch
        TransformBlock<TestContext, TestFailure?> runnerBlock = new(
            pending => RunTestAsync(pending, ct),
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = args.Parallelism, BoundedCapacity = args.Parallelism }
        );

        // send mismatch notifications
        ActionBlock<TestFailure?> notifyBlock = new(
            info => info is not null ? notifier.NotifyFailureAsync(info, ct) : Task.CompletedTask,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 50 }
        );

        headBuffer.LinkTo(filterBlock, propagate);
        filterBlock.LinkTo(runnerBlock, propagate);
        runnerBlock.LinkTo(notifyBlock, propagate);

        return (headBuffer, notifyBlock);
    }

    private IEnumerable<TestContext> FilterTests(BlockInfo head)
    {
        foreach (TestDefinition def in _tests)
        {
            bool shouldRun = false;
            TestContext ctx = new(def, head);

            try
            {
                shouldRun = def.Run.Compile(ctx);
            }
            catch (Exception ex)
            {
                _errorReporter.Report($"Error on test \"{def.FilePath}\" run condition evaluation", ex);
            }

            if (shouldRun)
                yield return ctx;
        }
    }

    private async Task<TestFailure?> RunTestAsync(TestContext test, CancellationToken ct)
    {
        try
        {
            if (await _executor.ExecuteAsync(args, test, ct) is not { } testFailure)
                return null;

            stats.RecordTestFailure();
            Console.Error.WriteLine($"Mismatch on test \"{test.Definition.FilePath}\" at block #{test.Head:#}");

            return testFailure with { RecentReorgs = reorgTracker.GetReorgs(ReorgsPeriodOnFail) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _errorReporter.Report($"Error on test \"{test.Definition.FilePath}\" invocation at block #{test.Head:#}", ex);
            return null;
        }
    }
}
