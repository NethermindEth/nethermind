// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;

namespace Nethermind.Config;

public interface IProcessExitSource
{
    public void Exit(int exitCode);

    public CancellationToken Token { get; }
}

public class ProcessExitSource : IProcessExitSource
{
    private static readonly CancellationToken _cancelledToken = new(canceled: true);
    private CancellationTokenSource _cancellationTokenSource;
    private readonly TaskCompletionSource _exitResult = new();

    public ProcessExitSource(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationTokenSource.Token.Register(() => Exit(ExitCodes.SigInt));
    }

    public void Exit(int exitCode)
    {
        if (CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellationTokenSource))
        {
            ExitCode = exitCode;
            _exitResult.SetResult();
        }
    }

    public int ExitCode { get; set; } = ExitCodes.Ok;

    public Task ExitTask => _exitResult.Task;

    public CancellationToken Token => _cancellationTokenSource?.Token ?? _cancelledToken;
}
