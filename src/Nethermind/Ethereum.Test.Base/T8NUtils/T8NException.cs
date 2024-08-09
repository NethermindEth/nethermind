using System;
using Nethermind.Core.Exceptions;

namespace Ethereum.Test.Base.T8NUtils;

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
