using System;

namespace Nethermind.Core2
{
    public class EmptyDisposable : IDisposable
    {
        private EmptyDisposable()
        {
        }

        public static IDisposable Instance { get; } = new EmptyDisposable();
        
        public void Dispose()
        {
        }
    }
}