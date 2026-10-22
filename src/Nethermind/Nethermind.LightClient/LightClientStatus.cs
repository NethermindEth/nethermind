// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Nethermind.Core;

namespace Nethermind.LightClient;

internal enum StartupStage { Checkpoint, Beacon, Bootstrap, Execution, Running, Stopping, Failed }

internal sealed record HeadProgress(ulong BeaconSlot, ulong BlockNumber, string BlockHash, bool Fresh);

internal sealed record ClientProgress(string StartupStage, bool RpcInitialized, HeadProgress? Latest,
    HeadProgress? Finalized, HostedAccountProgress[] HostedAccounts, HistoryProgress? History);

internal sealed class LightClientStatus(ulong chainId, Address[] accounts,
    Func<VerifiedHead?, bool> isLatestFresh, Func<VerifiedHead?, bool> isFinalizedFresh)
{
    private Runtime? _runtime;
    private int _stage = (int)StartupStage.Checkpoint;

    internal void SetStage(StartupStage stage) => Volatile.Write(ref _stage, (int)stage);

    internal void Initialize(Func<string, JsonElement, CancellationToken, Task<object>> invoke,
        Func<HttpContext, Task> handleWebSocket, Func<VerifiedHead?> getLatest, Func<VerifiedHead?> getFinalized,
        Func<VerifiedHead?, HostedAccountProgress[]> getHostedAccounts, Func<HistoryProgress> getHistory)
    {
        Volatile.Write(ref _runtime, new(invoke, handleWebSocket, getLatest, getFinalized, getHostedAccounts, getHistory));
        SetStage(StartupStage.Running);
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

    internal Task<object> InvokeAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartupStage stage = (StartupStage)Volatile.Read(ref _stage);
        if (stage is not (StartupStage.Stopping or StartupStage.Failed) && Volatile.Read(ref _runtime) is Runtime runtime)
            return runtime.Invoke(method, parameters, cancellationToken);
        if (method is "eth_chainId" or "eth_syncing")
        {
            if (parameters.ValueKind != JsonValueKind.Undefined &&
                (parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() != 0))
                throw new RpcException(-32602, "Invalid parameters.");
            if (method == "eth_chainId")
                return Task.FromResult<object>("0x" + chainId.ToString("x", CultureInfo.InvariantCulture));
            return Task.FromResult<object>(new { startingBlock = "0x0", currentBlock = "0x0", highestBlock = "0x0" });
        }
        throw new RpcException(-32000, $"Light client is {stage.ToString().ToLowerInvariant()}; check GET /status for authenticated head, account and history progress.");
    }

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

    private sealed record Runtime(Func<string, JsonElement, CancellationToken, Task<object>> Invoke,
        Func<HttpContext, Task> HandleWebSocket, Func<VerifiedHead?> GetLatest, Func<VerifiedHead?> GetFinalized,
        Func<VerifiedHead?, HostedAccountProgress[]> GetHostedAccounts, Func<HistoryProgress> GetHistory);
}
