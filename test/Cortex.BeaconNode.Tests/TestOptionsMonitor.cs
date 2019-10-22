using System;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Tests
{
    public static class TestOptionsMonitor
    {
        public static IOptionsMonitor<T> Create<T>(T options)
            where T : class, new()
        {
            return new TestOptionsMonitor<T>(options);
        }
    }

    public class TestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class, new()
    {
        public TestOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }

        public T Get(string name)
        {
            return CurrentValue;
        }

        public IDisposable OnChange(Action<T, string> listener)
        {
            return new TestChangeDisposable();
        }

        public class TestChangeDisposable : IDisposable
        {
            protected virtual void Dispose(bool disposing)
            {
            }

            public void Dispose()
            {
                Dispose(true);
            }
        }
    }
}
