// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.Runner.JsonRpc
{
    public class JsonRpcIpcRunner : IDisposable
    {
        private const int OperationCancelledError = 125;
        private readonly ILogger _logger;
        private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private readonly IJsonRpcConfig _jsonRpcConfig;

        private string _path;
        private Socket _server;

        public JsonRpcIpcRunner(
            IJsonRpcProcessor jsonRpcProcessor,
            IConfigProvider configurationProvider,
            ILogManager logManager,
            IJsonRpcLocalStats jsonRpcLocalStats,
            IJsonSerializer jsonSerializer,
            IFileSystem fileSystem)
        {
            _jsonRpcConfig = configurationProvider.GetConfig<IJsonRpcConfig>();
            _jsonRpcProcessor = jsonRpcProcessor;
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
                if (_logger.IsInfo) _logger.Info($"Starting the JSON-RPC over an IPC service: {_path}");

                Task.Factory.StartNew(_ => StartServer(_path, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning);
            }
        }

        private async Task StartServer(string path, CancellationToken cancellationToken)
        {
            try
            {
                DeleteSocketFileIfExists(path);

                _server = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _server.Bind(new UnixDomainSocketEndPoint(path));
                TryRestrictSocketPermissions(path);
                _server.Listen(0);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Failed to start IPC server at {path}.", ex);
                return;
            }

            while (true)
            {
                if (_logger.IsInfo) _logger.Info($"Waiting for an IPC connection...");

                Socket socket = await _server.AcceptAsync(cancellationToken);

                socket.ReceiveTimeout = _jsonRpcConfig.Timeout;
                socket.SendTimeout = _jsonRpcConfig.Timeout;

                _ = Task.Run(async () => await HandleIpcConnection(socket, cancellationToken));
            }
        }

        private void TryRestrictSocketPermissions(string path)
        {
            if (!_jsonRpcConfig.RestrictIpcSocketPermissions)
            {
                if (_logger.IsTrace) _logger.Trace("IPC socket permission restriction disabled by configuration.");
                return;
            }

            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 600 (rw-------)

                    if (_logger.IsTrace) _logger.Trace($"Restricted IPC socket permissions to 600 at {path}.");
                }
                else if (OperatingSystem.IsWindows())
                {
                    if (_logger.IsTrace) _logger.Trace("IPC socket on Windows uses named pipes with OS-level access control; skipping Unix permission setting.");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace("Unknown OS; skipping IPC socket permission restriction.");
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to set restrictive permissions on IPC socket at {path}: {ex}");
            }
        }


        private async Task HandleIpcConnection(Socket socket, CancellationToken cancellationToken)
        {
            using JsonRpcSocketsClient<IpcSocketMessageStream>? socketsClient = new(
                string.Empty,
                new IpcSocketMessageStream(socket),
                RpcEndpoint.IPC,
                _jsonRpcProcessor,
                _jsonRpcLocalStats,
                _jsonSerializer,
                maxBatchResponseBodySize: _jsonRpcConfig.MaxBatchResponseBodySize,
                concurrency: _jsonRpcConfig.IpcProcessingConcurrency);

            try
            {
                await socketsClient.ReceiveLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsDebug) _logger.Debug("Connection was cancelled.");
            }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                if (_logger.IsDebug) _logger.Debug("IPC client disconnected.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.ErrorCode == OperationCancelledError)
            {
                if (_logger.IsDebug) _logger.Debug("IPC client disconnected.");
            }
            catch (SocketException ex)
            {
                if (_logger.IsError) _logger.Error($"IPC server error {ex.ErrorCode}:", ex);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"IPC server error:", ex);
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
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot delete Unix socket file at {path}. {ex.Message}");
            }
        }

        public void Dispose()
        {
            _server?.Dispose();
            DeleteSocketFileIfExists(_path);
            if (_logger.IsInfo) _logger.Info("IPC JSON RPC service stopped");
        }
    }
}
