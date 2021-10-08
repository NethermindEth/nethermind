using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Plugins.WebSockets
{
    public class NdmWebSocketsPlugin : INdmWebSocketsPlugin
    {
        public string? Scheme { get; private set; }
        public string? Host { get; private set;}
        public int? Port { get; private set; }
        public string? Name { get; private set; }
        public string? Type { get; private set; }
        public string? Resource { get; private set; }
        public IWebSocketsAdapter? WebSocketsAdapter { get; set; } 
        private bool _initialized = false; 
        private ILogger? _logger;

        public Task InitAsync(ILogManager logManager)
        {
            if(string.IsNullOrEmpty(Host))
            {
                throw new Exception($"Host was not specified for NDM websockets plugin: {Name}");
            }

            if(Port == null)
            {
                throw new Exception($"Port was not specified for NDM websockets plugin: {Name}, Host: {Host}");
            }

            _logger = logManager.GetClassLogger();

            if(WebSocketsAdapter == null)
            {
                WebSocketsAdapter = new WebSocketsAdapter();
            }

            if(_logger.IsInfo) _logger.Info($"Initialized NDM websockets plugin: {Name}");

            _initialized = true;
            return Task.CompletedTask;
        }

        public Task<string?> QueryAsync(IEnumerable<string> args)
        {
            throw new NotImplementedException();
        }

        public async Task SubscribeAsync(Action<string> callback, Func<bool> enabled, IEnumerable<string> args, CancellationToken? token = null)
        {
            if(!_initialized)
            {
                throw new Exception($"Plugin: {Name} has not been initialized yet.");
            }

            var cancellationToken = token ?? CancellationToken.None;
            var uri = BuildUri();
            await WebSocketsAdapter.ConnectAsync(uri, cancellationToken);

            if(_logger.IsInfo) _logger.Info($"Connected to websockets host: {Host}");

            ArraySegment<byte> bytesToReceive = new ArraySegment<byte>();

            while(WebSocketsAdapter.State == WebSocketState.Open && enabled() && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await WaitForWebsocketsResponseAsync(bytesToReceive, cancellationToken, callback);
                }
                catch(ArgumentNullException)
                {
                    continue;
                }
            }

            await WebSocketsAdapter.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
        }

        private Uri BuildUri()
        {
            var uriBuilder = new UriBuilder(Scheme, Host, (int)Port);
            var nftUri = uriBuilder + Resource;
            return new Uri(nftUri);
        }

        private async Task WaitForWebsocketsResponseAsync(ArraySegment<byte> bytesToReceive, CancellationToken cancellationToken, Action<string> callback)
        {
            var responseMesage = await WebSocketsAdapter.ReceiveAsync(cancellationToken);
            callback(responseMesage);
        }
    }
}