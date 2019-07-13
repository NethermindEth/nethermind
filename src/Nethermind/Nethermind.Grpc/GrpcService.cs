using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core.Crypto;
using Nethermind.Grpc.Models;
using Nethermind.Logging;

namespace Nethermind.Grpc
{
    public class GrpcService : NethermindService.NethermindServiceBase, IGrpcService
    {
        private readonly ConcurrentDictionary<Keccak, string> _plugins =
            new ConcurrentDictionary<Keccak, string>();
        
        private readonly ConcurrentDictionary<string, NdmExtensionDetails> _ndmExtensions =
            new ConcurrentDictionary<string, NdmExtensionDetails>();

        private readonly ConcurrentDictionary<Keccak, NdmExtensionDetails> _dataHeadersExtensions =
            new ConcurrentDictionary<Keccak, NdmExtensionDetails>();

        private readonly ConcurrentDictionary<Keccak, ConcurrentDictionary<string, BlockingCollection<string>>>
            _depositsPeers =
                new ConcurrentDictionary<Keccak, ConcurrentDictionary<string, BlockingCollection<string>>>();

        private static readonly Empty Empty = new Empty();
        private readonly ILogger _logger;

        public void SetPlugin(string name, Keccak headerId)
        {
            _plugins.TryAdd(headerId, name);
        }

        public event EventHandler<NdmQueryDataEventArgs> NdmQueryDataReceived;

        public GrpcService(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
        }

        public override async Task InitNdmExtension(NdmExtension request, IServerStreamWriter<NdmQuery> responseStream,
            ServerCallContext context)
        {
            var peer = context.Peer;
            if (_logger.IsInfo) _logger.Info($"Received InitNdmExtension() GRPC call from peer: '{peer}'. Extension: '{request.Name}', type: '{request.Type}', accept all headers: {request.AcceptAllHeaders}, accepted headers: {request.AcceptedHeaders}.");
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Type))
            {
                if (_logger.IsWarn) _logger.Warn($"Extension name and type cannot be empty.");
                return;
            }

            var extension = _ndmExtensions.AddOrUpdate(request.Name.ToLowerInvariant(),
                _ => new NdmExtensionDetails(request),
                (_, e) => e);
            extension.Connect();
            try
            {
                var acceptedHeaders = request.AcceptedHeaders?.Where(h => !string.IsNullOrEmpty(h))
                                          .Select(h => new Keccak(h)) ?? Enumerable.Empty<Keccak>();
                foreach (var headerId in acceptedHeaders)
                {
                    _dataHeadersExtensions.TryAdd(headerId, extension);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Received an invalid header ID from peer: '{peer}'", ex);
                return;
            }

            QueryArgs query = null;
            try
            {
                while (true)
                {
                    query = extension.Queries.Take();
                    if (_logger.IsTrace) _logger.Trace($"Sending query for header: '{query.HeaderId}', deposit: '{query.DepositId}', args: {string.Join(", ", query.Args)}, iterations: {query.Iterations}");
                    await responseStream.WriteAsync(new NdmQuery
                    {
                        Iterations = query.Iterations,
                        HeaderId = query.HeaderId.ToString(),
                        DepositId = query.DepositId.ToString(),
                        Args = {query.Args}
                    });
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error(ex.ToString(), ex);
                if (!(query is null))
                {
                    extension.Queries.Add(query);
                    if (_logger.IsInfo) _logger.Info( $"Queued again query for header: '{query.HeaderId}', deposit: '{query.DepositId}'.");
                }
            }

            if (_logger.IsInfo) _logger.Info($"Finished using an extension: '{request.Name}' [{request.Type}].");
        }

        public override Task<Empty> SendNdmData(NdmQueryData request, ServerCallContext context)
        {
            var query = request.Query;
            if (_logger.IsTrace) _logger.Trace($"Received SendNdmData() GRPC call from peer: '{context.Peer}'. Query for header: '{query.HeaderId}', deposit: '{query.DepositId}', args: {string.Join(", ", query.Args)}, iterations: {query.Iterations}");
            NdmQueryDataReceived?.Invoke(this, new NdmQueryDataEventArgs(request));

            return Task.FromResult(Empty);
        }

        public override async Task SubscribeNdmData(NdmDataSubscription request,
            IServerStreamWriter<NdmDataResponse> responseStream, ServerCallContext context)
        {
            var peer = context.Peer;
            if (_logger.IsInfo) _logger.Info($"Received SubscribeNdmData() GRPC call from peer: '{peer}' for deposit: '{request.DepositId}'");
            var depositId = Keccak.TryParse(request.DepositId);
            if (depositId is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Received an invalid deposit ID from peer: '{peer}'.");
                return;
            }
            
            var depositPeers = _depositsPeers.AddOrUpdate(depositId, _ => new ConcurrentDictionary<string, BlockingCollection<string>>(), 
                (_, p) => p);
            var receivedData = depositPeers.AddOrUpdate(peer, _ => new BlockingCollection<string>(), (_, p) => p);
            
            try
            {
                while (true)
                {
                    var data = receivedData.Take();
                    await responseStream.WriteAsync(new NdmDataResponse
                    {
                        DepositId = request.DepositId,
                        Data = data
                    });
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error(ex.ToString(), ex);
                depositPeers.TryRemove(peer, out _);
                if (depositPeers.IsEmpty)
                {
                    _depositsPeers.TryRemove(depositId, out _);
                }
            }
        }

        public Task SendNdmQueryAsync(Keccak headerId, Keccak depositId, IEnumerable<string> args, uint iterations = 1)
        {
            if (!_plugins.TryGetValue(headerId, out var plugin))
            {
                return Task.CompletedTask;
            }

            var extension = _ndmExtensions.FirstOrDefault(e =>
                e.Value.Extension.Name.Equals(plugin, StringComparison.InvariantCultureIgnoreCase)).Value;
            
            if (extension is null)
            {
                if (_logger.IsTrace) _logger.Trace($"Extension was not found to handle a query for header: '{headerId}', deposit: '{depositId}'.");
                return Task.CompletedTask;
            }

            extension.Queries.Add(new QueryArgs(headerId, depositId, args, iterations));
            if (_logger.IsTrace) _logger.Trace($"Queued query for header: '{headerId}', deposit: '{depositId}'.");
            
            return Task.CompletedTask;
        }

        public Task SendNdmDataAsync(Keccak depositId, string data)
        {
            if (!_depositsPeers.TryGetValue(depositId, out var depositPeers))
            {
                if (_logger.IsTrace) _logger.Trace($"No GRPC client connection for deposit: '{depositId}' to receive the data.");
                return Task.CompletedTask;
            }

            foreach (var depositPeer in depositPeers)
            {
                depositPeer.Value.Add(data);
            }
            
            return Task.CompletedTask;
        }
        
        private class QueryArgs
        {
            public Keccak HeaderId { get; }
            public Keccak DepositId { get; }
            public IEnumerable<string> Args { get; }
            public uint Iterations { get; }

            public QueryArgs(Keccak headerId, Keccak depositId, IEnumerable<string> args, uint iterations = 1)
            {
                HeaderId = headerId;
                DepositId = depositId;
                Args = args;
                Iterations = iterations;
            }
        }

        private class NdmExtensionDetails
        {
            private int _connected;
            public NdmExtension Extension { get; }
            public BlockingCollection<QueryArgs> Queries { get; }
            public bool Connected => _connected == 1;

            public NdmExtensionDetails(NdmExtension extension)
            {
                Extension = extension;
                Queries = new BlockingCollection<QueryArgs>();
            }

            public void Connect() => Interlocked.Exchange(ref _connected, 1);

            public void Disconnect() => Interlocked.Exchange(ref _connected, 0);
        }
    }
}