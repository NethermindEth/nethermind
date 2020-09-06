using System;
using System.Threading;

namespace Nethermind.RocksDbExtractor.ProviderDecoders.Domain
{
    public class SessionClient
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private int _streamEnabled;
        private string[] _args;
        private string Id { get; }

        public SessionClient(string id, bool streamEnabled = false, string[]? args = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Invalid session client id.") : id;
            _args = args ?? Array.Empty<string>();
            if (streamEnabled)
            {
                EnableStream(_args);
            }
        }

        private void EnableStream(string[] args)
        {
            Interlocked.Exchange(ref _streamEnabled, 1);
            Interlocked.Exchange(ref _args, args);
            _cancellationTokenSource = new CancellationTokenSource();
        }
    }
}
