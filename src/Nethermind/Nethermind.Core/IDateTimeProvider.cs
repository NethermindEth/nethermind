using System;

namespace Nethermind.Core
{
    public interface IDateTimeProvider
    {
        DateTime UtcNow { get; }
    }
}