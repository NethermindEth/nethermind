// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Imports the configured EIP-8347 anchor in the background, then starts the BAL follower.</summary>
/// <remarks>
/// Main processing keeps running on flat alone while PBT lacks the base state (see <see cref="MigrationBackendSelector"/>),
/// so the node does not wait for the import. An import that fails validation stops the node, as it did when the import
/// blocked startup. Any other failure is logged and leaves PBT empty: flat keeps processing until activation, where
/// processing stalls.
/// </remarks>
internal sealed class PbtMigrationImport(Func<CancellationToken, Task> import, IProcessExitSource processExitSource, ILogManager logManager) : IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtMigrationImport>();
    private readonly CancellationTokenSource _cancellation = new();
    private string? _error;

    public PbtMigrationImport(PbtMigrationBootstrap bootstrap, PbtBalFollowerScheduler follower, IProcessExitSource processExitSource, ILogManager logManager)
        : this(async token =>
        {
            await bootstrap.Initialize(token);
            follower.Start();
        }, processExitSource, logManager)
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
            if (exception is InvalidDataException or IExceptionWithExitCode)
            {
                if (_logger.IsError) _logger.Error("EIP-8347 migration anchor failed validation; stopping the node.", exception);
                processExitSource.Exit(exception is IExceptionWithExitCode withExitCode ? withExitCode.ExitCode : ExitCodes.GeneralError);
            }
            else if (_logger.IsError) _logger.Error("EIP-8347 migration anchor import failed; PBT stays empty and processing will stall at activation.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        await Completion;
    }
}
