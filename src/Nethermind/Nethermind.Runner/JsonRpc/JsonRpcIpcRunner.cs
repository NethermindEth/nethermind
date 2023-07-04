// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using Newtonsoft.Json;

namespace Nethermind.Runner.JsonRpc
{
    public class JsonRpcIpcRunner : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private readonly IJsonRpcService _jsonRpcService;
        private readonly IJsonRpcConfig _jsonRpcConfig;

        private string _path;
        private Socket _server;
        private readonly ManualResetEvent _resetEvent = new(false);

        public JsonRpcIpcRunner(
            IJsonRpcProcessor jsonRpcProcessor,
            IJsonRpcService jsonRpcService,
            IConfigProvider configurationProvider,
            ILogManager logManager,
            IJsonRpcLocalStats jsonRpcLocalStats,
            IJsonSerializer jsonSerializer,
            IFileSystem fileSystem)
        {
            _jsonRpcConfig = configurationProvider.GetConfig<IJsonRpcConfig>();
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonRpcService = jsonRpcService;
            _logger = logManager.GetClassLogger();
            _jsonRpcLocalStats = jsonRpcLocalStats;
            _jsonSerializer = jsonSerializer;
            _fileSystem = fileSystem;
        }

        public void Start(CancellationToken cancellationToken)
        {
            _path = _jsonRpcConfig.IpcUnixDomainSocketPath;

            if (!string.IsNullOrEmpty(_path))
            {
                _logger.Info($"Starting IPC JSON RPC service over '{_path}'");

                Task.Factory.StartNew(_ => StartServer(_path), cancellationToken, TaskCreationOptions.LongRunning);
            }
        }

        private void StartServer(string path)
        {
            try
            {
                DeleteSocketFileIfExists(path);

                var endPoint = new UnixDomainSocketEndPoint(path);

                _server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                _server.Bind(endPoint);
                _server.Listen(0);

                while (true)
                {
                    _resetEvent.Reset();

                    _logger.Info("Waiting for a IPC connection...");
                    _server.BeginAccept(AcceptCallback, null);

                    _resetEvent.WaitOne();
                }
            }
            catch (IOException exc) when (exc.InnerException is not null && exc.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogDebug("Client disconnected.");
            }
            catch (SocketException exc) when (exc.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogDebug("Client disconnected.");
            }
            catch (SocketException exc)
            {
                _logger.Error($"Error ({exc.ErrorCode}) when starting IPC server over '{_path}' path.", exc);
            }
            catch (Exception exc)
            {
                _logger.Error($"Error when starting IPC server over '{_path}' path.", exc);
            }
            finally
            {
                Dispose();
            }
        }

        private async void AcceptCallback(IAsyncResult ar)
        {
            JsonRpcSocketsClient socketsClient = null;

            try
            {
                Socket socket = _server.EndAccept(ar);
                socket.ReceiveTimeout = _jsonRpcConfig.Timeout;
                socket.SendTimeout = _jsonRpcConfig.Timeout;

                _resetEvent.Set();

                socketsClient = new JsonRpcSocketsClient(
                    string.Empty,
                    new IpcSocketsHandler(socket),
                    RpcEndpoint.IPC,
                    _jsonRpcProcessor,
                    _jsonRpcService,
                    _jsonRpcLocalStats,
                    _jsonSerializer,
                    maxBatchResponseBodySize: _jsonRpcConfig.MaxBatchResponseBodySize);

                await socketsClient.ReceiveAsync();
            }
            catch (IOException exc) when (exc.InnerException is not null && exc.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogDebug("Client disconnected.");
            }
            catch (SocketException exc) when (exc.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogDebug("Client disconnected.");
            }
            catch (SocketException exc)
            {
                _logger.Warn($"Error {exc.ErrorCode}:{exc.Message}");
            }
            catch (Exception exc)
            {
                _logger.Error("Error when handling IPC communication with a client.", exc);
            }
            finally
            {
                socketsClient?.Dispose();
            }
        }

        private void DeleteSocketFileIfExists(string path)
        {
            try
            {
                if (_fileSystem.File.Exists(path))
                {
                    _fileSystem.File.Delete(path);
                }
            }
            catch (Exception exc)
            {
                _logger.Warn($"Cannot delete UNIX socket file:{path}. {exc.Message}");
            }
        }

        public void Dispose()
        {
            _server?.Dispose();
            DeleteSocketFileIfExists(_path);
            if (_logger.IsInfo) _logger.Info("IPC JSON RPC service stopped");
        }

        private void LogDebug(string msg)
        {
            if (_logger.IsDebug) _logger.Debug(msg);
        }
    }
}
