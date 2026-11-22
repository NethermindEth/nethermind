// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Facade.Eth;

namespace Nethermind.LightClient.Rpc;

internal enum StartupStage { Checkpoint, Beacon, Bootstrap, Execution, Running, Stopping, Failed }

internal sealed record HeadProgress(ulong BeaconSlot, ulong BlockNumber, string BlockHash, bool Fresh);

internal sealed record ClientProgress(string StartupStage, bool RpcInitialized, HeadProgress? Latest,
    HeadProgress? Finalized, HostedAccountProgress[] HostedAccounts, HistoryProgress? History);

internal sealed class LightClientStatus(ulong chainId, Address[] accounts,
    Func<VerifiedHead?, bool> isLatestFresh, Func<VerifiedHead?, bool> isFinalizedFresh)
{
    private readonly Lock _lifecycle = new();
    private readonly TaskCompletionSource _startupCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Runtime? _runtime;
    private int _stage = (int)StartupStage.Checkpoint;

    internal void SetStage(StartupStage stage)
    {
        bool terminal = stage is StartupStage.Stopping or StartupStage.Failed;
        lock (_lifecycle)
        {
            if (!terminal && (StartupStage)_stage is (StartupStage.Stopping or StartupStage.Failed)) return;
            Volatile.Write(ref _stage, (int)stage);
        }
        if (terminal) _startupCompleted.TrySetResult();
    }

    internal ulong ChainId => chainId;
    internal bool Listening => (StartupStage)Volatile.Read(ref _stage) is not (StartupStage.Stopping or StartupStage.Failed)
        && Volatile.Read(ref _runtime) is not null;
    internal int PeerCount => Listening ? Volatile.Read(ref _runtime)?.Backend.PeerCount ?? 0 : 0;

    internal void Initialize(RpcBackend backend, Func<HttpContext, Task> handleWebSocket,
        Func<VerifiedHead?> getLatest, Func<VerifiedHead?> getFinalized,
        Func<VerifiedHead?, HostedAccountProgress[]> getHostedAccounts, Func<HistoryProgress> getHistory)
    {
        Runtime runtime = new(backend, handleWebSocket, getLatest, getFinalized, getHostedAccounts, getHistory);
        lock (_lifecycle)
        {
            if ((StartupStage)_stage is StartupStage.Stopping or StartupStage.Failed) return;
            Volatile.Write(ref _runtime, runtime);
            Volatile.Write(ref _stage, (int)StartupStage.Running);
        }
        _startupCompleted.TrySetResult();
    }

    internal SyncingResult GetSyncing()
    {
        StartupStage stage = (StartupStage)Volatile.Read(ref _stage);
        if (stage is not (StartupStage.Stopping or StartupStage.Failed) && Volatile.Read(ref _runtime)?.Backend is { } backend)
            return backend.GetSyncing();
        return new() { IsSyncing = true };
    }

    internal ValueTask<RpcBackend> GetBackendAsync(CancellationToken token, bool waitForStartup)
    {
        token.ThrowIfCancellationRequested();
        StartupStage stage = (StartupStage)Volatile.Read(ref _stage);
        Runtime? runtime = Volatile.Read(ref _runtime);
        if (stage is not (StartupStage.Stopping or StartupStage.Failed) && runtime?.Backend is { } backend)
            return ValueTask.FromResult(backend);
        if (!waitForStartup || stage is StartupStage.Stopping or StartupStage.Failed)
            throw Unavailable(stage);
        return WaitForBackendAsync(token);
    }

    private async ValueTask<RpcBackend> WaitForBackendAsync(CancellationToken token)
    {
        await _startupCompleted.Task.WaitAsync(token);
        return await GetBackendAsync(token, waitForStartup: false);
    }

    internal ClientProgress Read()
    {
        StartupStage stage = (StartupStage)Volatile.Read(ref _stage);
        Runtime? runtime = Volatile.Read(ref _runtime);
        VerifiedHead? latest = runtime?.GetLatest();
        VerifiedHead? finalized = runtime?.GetFinalized();
        HostedAccountProgress[] hosted;
        if (runtime is not null) hosted = runtime.GetHostedAccounts(latest);
        else
        {
            hosted = new HostedAccountProgress[accounts.Length];
            for (int i = 0; i < accounts.Length; i++)
                hosted[i] = new(accounts[i].ToString(), null, null, null, 0, 0, false, "waiting", null, null);
        }
        return new(stage.ToString().ToLowerInvariant(), runtime is not null,
            Progress(latest, isLatestFresh), Progress(finalized, isFinalizedFresh), hosted, runtime?.GetHistory());

        static HeadProgress? Progress(VerifiedHead? head, Func<VerifiedHead?, bool> isFresh) =>
            head is null ? null : new(head.Slot, head.Number, head.BlockHash.ToString(), isFresh(head));
    }

    private static RpcException Unavailable(StartupStage stage) =>
        new(-32000, $"Light client is {stage.ToString().ToLowerInvariant()}; check GET /status for authenticated head, account and history progress.");

    internal Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }
        StartupStage stage = (StartupStage)Volatile.Read(ref _stage);
        if (stage is not (StartupStage.Stopping or StartupStage.Failed) && Volatile.Read(ref _runtime) is Runtime runtime)
            return runtime.HandleWebSocket(context);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return context.Response.WriteAsJsonAsync(Read(), context.RequestAborted);
    }

    private sealed record Runtime(RpcBackend Backend, Func<HttpContext, Task> HandleWebSocket,
        Func<VerifiedHead?> GetLatest, Func<VerifiedHead?> GetFinalized,
        Func<VerifiedHead?, HostedAccountProgress[]> GetHostedAccounts, Func<HistoryProgress> GetHistory);
}
