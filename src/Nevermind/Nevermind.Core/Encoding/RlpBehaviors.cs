using System;

namespace Nevermind.Core.Encoding
{
    [Flags]
    public enum RlpBehaviors
    {
        None,
        AllowExtraData,
        All
    }
}