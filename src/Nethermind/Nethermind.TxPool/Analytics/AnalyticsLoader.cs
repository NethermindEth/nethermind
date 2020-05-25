using System;

namespace Nethermind.TxPool.Analytics
{
    [AttributeUsage(AttributeTargets.Class)]
    public class AnalyticsLoaderAttribute : Attribute
    {
        public AnalyticsLoaderAttribute()
        {
        }
    }
}