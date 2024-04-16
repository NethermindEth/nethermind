// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;

namespace Evm.T8NTool;

public class T8NException : Exception, IExceptionWithExitCode
{
    public T8NException(Exception e, int exitCode) : base(e.Message)
    {
        ExitCode = exitCode;
    }

    public T8NException(string message, int exitCode) : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
