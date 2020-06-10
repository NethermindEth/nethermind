using System;

namespace Nethermind.Blockchain.Analytics
{
    [AttributeUsage(AttributeTargets.Class)]
    public class AnalyticsLoaderAttribute : Attribute
    {
        public AnalyticsLoaderAttribute()
        {
        }
    }
}