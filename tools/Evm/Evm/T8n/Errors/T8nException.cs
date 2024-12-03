// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;

namespace Evm.T8n.Errors;

public class T8nException : Exception, IExceptionWithExitCode
{
    public T8nException(Exception e, int exitCode) : base(e.Message, e)
    {
        ExitCode = exitCode;
    }

    public T8nException(Exception e, string message, int exitCode) : base(message, e)
    {
        ExitCode = exitCode;
    }

    public T8nException(string message, int exitCode) : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
