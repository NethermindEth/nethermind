// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;

namespace RpcTestsMon;

internal record RequestContext(long Number);

internal record TestRun(HeadInfo Head, TestDefinition Definition, RequestContext RequestCtx);

internal record MismatchInfo(TestRun Test, JsonNode Request, JsonNode TargetResponse, JsonNode ReferenceResponse)
{
    public HeadInfo Head => Test.Head;
}

internal class MonitorRunner(INotifier notifier)
{
    private long _requestNumber;

    public async Task RunAsync(ExecutionArgs args, CancellationToken ct)
    {
        TestDefinition[] tests = TestLoader.Load(args.TestGlobs);
        Console.WriteLine($"Loaded {tests.Length} tests");
        if (tests.Length == 0) return;

        using HttpClient client = new();
        (ITargetBlock<HeadInfo> startBlock, IDataflowBlock endBlock) =
            BuildPipeline(args, tests, new RequestSender(client), ct);

        HeadMonitor headMonitor = new(args.TargetUrl, notifier);
        try
        {
            await foreach (HeadInfo head in headMonitor.SubscribeAsync(ct))
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

    private (ITargetBlock<HeadInfo> start, IDataflowBlock end) BuildPipeline(
        ExecutionArgs args, TestDefinition[] tests, RequestSender sender, CancellationToken ct)
    {
        BufferBlock<HeadInfo> headBuffer = new(new DataflowBlockOptions { BoundedCapacity = 2 });

        DataflowLinkOptions propagate = new() { PropagateCompletion = true };

        // filter tests that need to run for this head
        TransformManyBlock<HeadInfo, TestRun> filterBlock = new(
            head => FilterTests(tests, head),
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = tests.Length }
        );

        // run each triggered test against both nodes, yield on mismatch
        TransformBlock<TestRun, MismatchInfo?> runnerBlock = new(
            pending => RunTestAsync(pending, sender, args, ct),
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = args.Parallelism, BoundedCapacity = args.Parallelism }
        );

        // send mismatch notifications
        ActionBlock<MismatchInfo?> notifyBlock = new(
            info => info is not null ? notifier.NotifyMismatchAsync(info, ct) : Task.CompletedTask,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1 }
        );

        headBuffer.LinkTo(filterBlock, propagate);
        filterBlock.LinkTo(runnerBlock, propagate);
        runnerBlock.LinkTo(notifyBlock, propagate);

        return (headBuffer, notifyBlock);
    }

    private IEnumerable<TestRun> FilterTests(TestDefinition[] tests, HeadInfo head)
    {
        foreach (TestDefinition test in tests)
        {
            TestRun? pending = null;
            RequestContext request = new(++_requestNumber);

            try
            {
                // TODO: optimize via comparing simple value
                JsonNode? changed = test.OnChanged.Compile(head);
                if (changed?.ToString() == test.LastOnChangedValue?.ToString())
                    continue;

                test.LastOnChangedValue = changed;
                pending = new TestRun(head, test, request);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"onChanged evaluation error: {ex.Message}");
            }

            if (pending is not null)
                yield return pending;
        }
    }

    private async Task<MismatchInfo?> RunTestAsync(
        TestRun test, RequestSender sender, ExecutionArgs args, CancellationToken ct)
    {
        try
        {
            JsonNode request = test.Definition.Request.Compile(test.Head, test.RequestCtx)!;
            JsonNode targetResp = await sender.SendAsync(args.TargetUrl, request, ct);
            JsonNode refResp = await sender.SendAsync(args.ReferenceUrl, request, ct);

            if (ResponseComparer.Compare(targetResp, refResp))
                return null;

            Console.Error.WriteLine($"Mismatch on test \"{test.Definition.FilePath}\" at block #{test.Head:#}");
            return new MismatchInfo(test, request, targetResp, refResp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string errorMsg = $"Test \"{test.Definition.FilePath}\" error at block #{test.Head:#}: {ex.Message}";
            Console.Error.WriteLine(errorMsg);
            _ = notifier.NotifyErrorAsync(errorMsg);
            return null;
        }
    }
}
