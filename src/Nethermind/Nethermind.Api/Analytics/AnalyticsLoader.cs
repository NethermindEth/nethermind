using System;

namespace Nethermind.Api.Analytics
{
    [AttributeUsage(AttributeTargets.Class)]
    public class AnalyticsLoaderAttribute : Attribute
    {
        public AnalyticsLoaderAttribute()
        {
        }
    }
}