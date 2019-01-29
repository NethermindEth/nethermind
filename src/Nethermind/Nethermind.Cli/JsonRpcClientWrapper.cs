using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Cli
{
    public class JsonRpcClientWrapper : IJsonRpcClient
    {
        private readonly IJsonSerializer _serializer;
        private readonly ILogManager _logManager;
        private Dictionary<Uri, IJsonRpcClient> _clients = new Dictionary<Uri, IJsonRpcClient>();
        
        private IJsonRpcClient _currentClient;

        public JsonRpcClientWrapper(IJsonSerializer serializer, ILogManager logManager)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

        public string CurrentUri { get; set; }

        public void SwitchUri(Uri uri)
        {
            CurrentUri = uri.ToString();
            if (!_clients.ContainsKey(uri))
            {
                _clients[uri] = new BasicJsonRpcClient(uri, _serializer, _logManager);
            }

            _currentClient = _clients[uri];
        }

        public async Task<string> Post(string method, params object[] parameters)
        {
            return await _currentClient.Post(method, parameters);
        }

        public async Task<T> Post<T>(string method, params object[] parameters)
        {
            return await _currentClient.Post<T>(method, parameters);
        }
    }
}