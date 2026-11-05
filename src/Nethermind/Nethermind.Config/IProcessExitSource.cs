// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    public ProcessExitSource(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationTokenSource.Token.Register(() => Exit(ExitCodes.SigInt));
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

    public CancellationToken Token => _cancellationTokenSource?.Token ?? _cancelledToken;
}
