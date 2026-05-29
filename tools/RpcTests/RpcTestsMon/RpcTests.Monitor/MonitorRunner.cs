// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;
using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal record struct TestContext(TestDefinition Definition, BlockInfo? Head = null)
{
    public static string Hex(int n) => $"0x{n:x}";
    public static string Hex(long n) => $"0x{n:x}";

    public JsonNode? LastOnChangedValue;
}

internal record TestFailure(TestContext Test, JsonNode Request, JsonNode TargetResponse, JsonNode ReferenceResponse)
{
    public BlockInfo Head => Test.Head!;
}

internal class MonitorRunner(INotifier notifier)
{
    private TestContext[] _testContexts = [];

    public async Task RunAsync(ExecutionArgs args, CancellationToken ct)
    {
        TestDefinition[] tests = TestLoader.Load(args.TestGlobs);
        Console.WriteLine($"Loaded {tests.Length} tests");
        if (tests.Length == 0) return;

        _testContexts = [.. tests.Select(static t => new TestContext(t))];

        using HttpClient client = new();
        (ITargetBlock<BlockInfo> startBlock, IDataflowBlock endBlock) =
            BuildPipeline(args, new RequestSender(client), ct);

        HeadMonitor headMonitor = new(args.TargetUrl, notifier);
        try
        {
            await foreach (BlockInfo head in headMonitor.SubscribeAsync(ct))
            {
                //Console.WriteLine($"Head #{head:#}");
                if (!startBlock.Post(head))
                    Console.Error.WriteLine($"Head #{head:#} skipped — pipeline busy");
            }
        }
        catch (OperationCanceledException) { }

        startBlock.Complete();
        await endBlock.Completion;
    }

    private (ITargetBlock<BlockInfo> start, IDataflowBlock end) BuildPipeline(
        ExecutionArgs args, RequestSender sender, CancellationToken ct)
    {
        BufferBlock<BlockInfo> headBuffer = new(new DataflowBlockOptions { BoundedCapacity = 2 });

        DataflowLinkOptions propagate = new() { PropagateCompletion = true };

        // filter tests that need to run for this head
        TransformManyBlock<BlockInfo, TestContext> filterBlock = new(
            FilterTests,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = _testContexts.Length }
        );

        // run each triggered test against both nodes, yield on mismatch
        TransformBlock<TestContext, TestFailure?> runnerBlock = new(
            pending => RunTestAsync(pending, sender, args, ct),
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = args.Parallelism, BoundedCapacity = args.Parallelism }
        );

        // send mismatch notifications
        ActionBlock<TestFailure?> notifyBlock = new(
            info => info is not null ? notifier.NotifyFailureAsync(info, ct) : Task.CompletedTask,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1 }
        );

        headBuffer.LinkTo(filterBlock, propagate);
        filterBlock.LinkTo(runnerBlock, propagate);
        runnerBlock.LinkTo(notifyBlock, propagate);

        return (headBuffer, notifyBlock);
    }

    private IEnumerable<TestContext> FilterTests(BlockInfo head)
    {
        for (int i = 0; i < _testContexts.Length; i++)
        {
            ref TestContext state = ref _testContexts[i];
            TestContext? pending = null;
            try
            {
                // TODO: optimize via comparing simple value
                TestContext ctx = state with { Head = head };
                JsonNode? changed = state.Definition.OnChanged.Compile(ctx);
                if (!ValuesEqual(changed, state.LastOnChangedValue))
                    continue;

                state.LastOnChangedValue = changed;
                pending = ctx;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"onChanged evaluation error: {ex.Message}");
            }

            if (pending is not null)
                yield return pending.Value;
        }
    }

    private async Task<TestFailure?> RunTestAsync(
        TestContext test, RequestSender sender, ExecutionArgs args, CancellationToken ct)
    {
        try
        {
            JsonNode request = test.Definition.Request.Compile(test)!;
            JsonNode targetResp = await sender.SendAsync(args.TargetUrl, request, ct);
            JsonNode refResp = await sender.SendAsync(args.ReferenceUrl, request, ct);

            if (ResponseComparer.Compare(targetResp, refResp))
                return null;

            Console.Error.WriteLine($"Mismatch on test \"{test.Definition.FilePath}\" at block #{test.Head:#}");
            return new TestFailure(test, request, targetResp, refResp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string errorMsg = $"Test \"{test.Definition.FilePath}\" error at block #{test.Head:#}: {ex.Message}";
            Console.Error.WriteLine(errorMsg);
            _ = notifier.NotifyErrorAsync(errorMsg);
            return null;
        }
    }

    private static bool ValuesEqual(JsonNode? x, JsonNode? y) => (x?.GetValueKind(), y?.GetValueKind()) switch
    {
        (null, null) => true,
        (JsonValueKind.Null, JsonValueKind.Null) => true,
        (JsonValueKind.False, JsonValueKind.False) => true,
        (JsonValueKind.True, JsonValueKind.True) => true,
        (JsonValueKind.String, JsonValueKind.String) => x.AsValue().Equals(y.AsValue()),
        (JsonValueKind.Number, JsonValueKind.Number) => x.AsValue().Equals(y.AsValue()),
        _ => false
    };
}
