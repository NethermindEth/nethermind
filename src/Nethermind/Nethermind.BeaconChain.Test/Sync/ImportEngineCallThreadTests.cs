// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Test.Sync;

public class ImportEngineCallThreadTests
{
    [Test]
    [CancelAfter(60_000)]
    public async Task A_newPayload_that_answers_late_is_awaited_off_the_thread_pool(CancellationToken token)
    {
        SlowEngine engine = new();
        await using DeferredBlockColumnFetchTests.Fixture fixture = DeferredBlockColumnFetchTests.Fixture.Create(engine);
        foreach (ulong column in fixture.Sampled)
        {
            fixture.GiveColumn(column);
        }

        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();

        Task<BlockImportResult> import = Task.Run(() => orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token), token);
        await engine.Entered.Task.WaitAsync(token);
        bool completedWhileEngineWaited = import.IsCompleted;
        engine.Answer.Set();
        BlockImportResult result = await import;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(completedWhileEngineWaited, Is.False, "fixture: the import waits for the engine");
        Assert.That(engine.CalledOnPoolThread, Is.False, "the engine call runs on a thread of its own");
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
    }

    private sealed class SlowEngine : BlockImporterTests.ValidPayloadEngine
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Answer { get; } = new();
        public bool CalledOnPoolThread { get; private set; }

        public override ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            CalledOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            Entered.TrySetResult();
            if (!Answer.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The test never let newPayload answer");
            }

            HasAnsweredNewPayload = true;
            return ExecutionStatus.Valid;
        }
    }
}
