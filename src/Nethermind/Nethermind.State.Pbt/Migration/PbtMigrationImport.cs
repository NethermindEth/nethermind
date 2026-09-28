// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Imports the configured EIP-8347 anchor in the background, then starts the BAL follower.</summary>
/// <remarks>
/// Main processing keeps running on flat alone while PBT lacks the base state (see <see cref="MigrationBackendSelector"/>),
/// so the node does not wait for the import. A failed import is logged and leaves PBT empty: flat keeps processing
/// until activation, where processing stalls.
/// </remarks>
internal sealed class PbtMigrationImport(Func<CancellationToken, Task> import, ILogManager logManager) : IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtMigrationImport>();
    private readonly CancellationTokenSource _cancellation = new();
    private string? _error;

    public PbtMigrationImport(PbtMigrationBootstrap bootstrap, PbtBalFollowerScheduler follower, ILogManager logManager)
        : this(async token =>
        {
            await bootstrap.Initialize(token);
            follower.Start();
        }, logManager)
    {
    }

    /// <summary>Completes once the import has finished, failed or been cancelled.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public bool IsPending => !Completion.IsCompleted;

    public string? Error => Volatile.Read(ref _error);

    public void Start() => Completion = Task.Run(Run);

    private async Task Run()
    {
        try
        {
            await import(_cancellation.Token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Volatile.Write(ref _error, exception.Message);
            if (_logger.IsError) _logger.Error("EIP-8347 migration anchor import failed; PBT stays empty and processing will stall at activation.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        await Completion;
    }
}
