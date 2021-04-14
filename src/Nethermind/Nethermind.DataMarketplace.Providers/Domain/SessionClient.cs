using System;
using System.Threading;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class SessionClient
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private int _streamEnabled;
        private string[] _args;
        public string Id { get; }

        public bool StreamEnabled
        {
            get => _streamEnabled == 1;
            private set => _streamEnabled = value ? 1 : 0;
        }

        public string[] Args
        {
            get => _args;
            private set => _args = value;
        }

        public CancellationToken? CancellationToken => _cancellationTokenSource?.Token;

        public SessionClient(string id, bool streamEnabled = false, string[]? args = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Invalid session client id.") : id;
            _args = args ?? Array.Empty<string>();
            if (streamEnabled)
            {
                EnableStream(_args);
            }
        }

        public void EnableStream(string[] args)
        {
            Interlocked.Exchange(ref _streamEnabled, 1);
            Interlocked.Exchange(ref _args, args);
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public void DisableStream()
        {
            Interlocked.Exchange(ref _streamEnabled, 0);
            _cancellationTokenSource?.Cancel();
        }
    }
}