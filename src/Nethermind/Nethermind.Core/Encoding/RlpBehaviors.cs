using System;

namespace Nethermind.Core.Encoding
{
    [Flags]
    public enum RlpBehaviors
    {
        None,
        AllowExtraData,
        All
    }
}