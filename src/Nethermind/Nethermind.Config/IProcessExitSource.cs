// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Config;

public interface IProcessExitSource
{
    public void Exit(int exitCode);

    public CancellationToken Token { get; }
}

public class ProcessExitSource : IProcessExitSource
{
    private static readonly CancellationToken _cancelledToken = new(canceled: true);
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly TaskCompletionSource _exitResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProcessExitSource(CancellationToken cancellationToken) : this(cancellationToken, static () => ExitCodes.SigInt) { }

    /// <param name="cancellationToken">Cancelling it exits with the code from <paramref name="cancellationExitCode"/>.</param>
    /// <param name="cancellationExitCode">Gives the exit code when <paramref name="cancellationToken"/> is cancelled; called at that moment.</param>
    public ProcessExitSource(CancellationToken cancellationToken, Func<int> cancellationExitCode)
    {
        ArgumentNullException.ThrowIfNull(cancellationExitCode);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationTokenSource.Token.Register(() => Exit(cancellationExitCode()));
    }

    public void Exit(int exitCode)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref _cancellationTokenSource, null);
        if (source is null) return;

        try
        {
            source.Cancel();
        }
        finally
        {
            // A throwing callback must not leave shutdown waiting on ExitTask forever.
            source.Dispose();
            ExitCode = exitCode;
            _exitResult.SetResult();
        }
    }

    public int ExitCode { get; set; } = ExitCodes.Ok;

    public Task ExitTask => _exitResult.Task;

    public CancellationToken Token
    {
        get
        {
            try
            {
                return _cancellationTokenSource?.Token ?? _cancelledToken;
            }
            catch (ObjectDisposedException)
            {
                // Read just before Exit swapped the source out; Exit has cancelled it before disposing it.
                return _cancelledToken;
            }
        }
    }
}
