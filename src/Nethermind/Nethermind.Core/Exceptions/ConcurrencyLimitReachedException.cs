using System;

namespace Nethermind.Core.Exceptions;

public class ConcurrencyLimitReachedException : InvalidOperationException
{
    public ConcurrencyLimitReachedException(string message) : base(message)
    {
    }
}
