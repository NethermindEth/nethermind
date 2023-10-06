// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Config;

public interface IProcessExitSource
{
    public void Exit(int exitCode);
}

public class ProcessExitSource : IProcessExitSource
{
    private CancellationTokenSource _cancellationTokenSource = new();
    public int ExitCode { get; set; } = ExitCodes.Ok;

    public CancellationToken Token => _cancellationTokenSource!.Token;

    public void Exit(int exitCode)
    {
        if (CancellationTokenExtensions.CancelDisposeAndClear(ref _cancellationTokenSource))
        {
            ExitCode = exitCode;
        }
    }
}
