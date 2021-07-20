using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.Runner.JsonRpc
{
    public class JsonRpcIpcRunner
    {
        private readonly ILogger _logger;
        private readonly IConfigProvider _configurationProvider;
        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IJsonSerializer _jsonSerializer = new EthereumJsonSerializer();

        private string _path;
        private Socket _server;
        private ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public JsonRpcIpcRunner(
            IJsonRpcProcessor jsonRpcProcessor,
            IConfigProvider configurationProvider,
            ILogManager logManager)
        {
            _jsonRpcConfig = configurationProvider.GetConfig<IJsonRpcConfig>();
            _configurationProvider = configurationProvider;
            _jsonRpcProcessor = jsonRpcProcessor;
            _logger = logManager.GetClassLogger();
        }

        public void Start(CancellationToken cancellationToken)
        {
            _path = _jsonRpcConfig.IpcUnixDomainSocketPath;

            if (!string.IsNullOrEmpty(_path))
            {
                _logger.Info($"Starting IPC JSON RPC service over '{_path}'");

                var task = Task.Factory.StartNew((x) =>
                {
                    StartServer(_path);
                },
                cancellationToken,
                TaskCreationOptions.LongRunning);
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
                    _server.BeginAccept(new AsyncCallback(AcceptCallback), null);

                    _resetEvent.WaitOne();
                }
            }
            catch (IOException exc) when (exc.InnerException != null && exc.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogInfo("Client disconnected.");
            }
            catch (SocketException exc) when (exc.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogInfo("Client disconnected.");
            }
            catch (SocketException exc)
            {
                _logger.Error($"Error ({exc.ErrorCode}) when starting IPC server over '{_path}' path.", exc);
            }
            catch (Exception exc)
            {
                _logger.Error($"Error when starting IPC server over '{ _path}' path.", exc);
            }
            finally
            {
                Dispose();
            }
        }

        private void AcceptCallback(IAsyncResult ar)
        {
            Socket clientSocket = null;

            try
            {
                clientSocket = _server.EndAccept(ar);
                clientSocket.ReceiveTimeout = _jsonRpcConfig.Timeout;
                clientSocket.SendTimeout = _jsonRpcConfig.Timeout;

                _resetEvent.Set();

                StateObject state = new(clientSocket);
                clientSocket.BeginReceive(state.Buffer, 0, StateObject.BufferSize, SocketFlags.None, new AsyncCallback(ReadCallback), state);
            }
            catch (IOException exc) when (exc.InnerException != null && exc.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogInfo("Client disconnected.");
                clientSocket?.Dispose();
            }
            catch (SocketException exc) when (exc.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogInfo("Client disconnected.");
                clientSocket?.Dispose();
            }
            catch (SocketException exc)
            {
                _logger.Warn($"Error {exc.ErrorCode}:{exc.Message}");
                clientSocket?.Dispose();
            }
            catch (Exception exc)
            {
                _logger.Error("Error when handling IPC communication with a client.", exc);

                clientSocket?.Dispose();
            }
        }

        private async void ReadCallback(IAsyncResult ar)
        {
            Socket clientSocket = null;

            try
            {
                StateObject state = ar.AsyncState as StateObject;
                clientSocket = state.ClientSocket;

                // Read data from the client socket.  
                int read = clientSocket.EndReceive(ar);

                // Data was read from the client socket.  
                if (read > 0)
                {
                    Interlocked.Add(ref Nethermind.JsonRpc.Metrics.JsonRpcBytesReceivedIpc, read);

                    var incoming = Encoding.UTF8.GetString(state.Buffer, 0, read);
                    state.MsgBuilder.Append(incoming);

                    if (read < StateObject.BufferSize || read == StateObject.BufferSize && clientSocket.Available == 0)
                    {

                        // PROCESS the message
                        using JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync(state.MsgBuilder.ToString(), JsonRpcContext.IPC);
                        var serialized = result.IsCollection ? _jsonSerializer.Serialize(result.Responses) : _jsonSerializer.Serialize(result.Response);

                        var bytesToSend = Encoding.UTF8.GetBytes(serialized);                       
                        clientSocket.Send(bytesToSend);
                        Interlocked.Add(ref Nethermind.JsonRpc.Metrics.JsonRpcBytesSentIpc, bytesToSend.Length);

                        state.MsgBuilder.Clear();
                    }

                    clientSocket.BeginReceive(state.Buffer, 0, StateObject.BufferSize, 0, new AsyncCallback(ReadCallback), state);
                }
                else
                {
                    LogInfo("Closing IPC session.");
                    clientSocket.Dispose();
                }
            }
            catch (IOException exc) when (exc.InnerException != null && exc.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogInfo("Client disconnected.");
                clientSocket?.Dispose();
            }
            catch (SocketException exc) when (exc.SocketErrorCode == SocketError.ConnectionReset)
            {
                LogInfo("Client disconnected.");
                clientSocket?.Dispose();
            }
            catch (SocketException exc)
            {
                _logger.Warn($"Error {exc.ErrorCode}:{exc.Message}");
                clientSocket?.Dispose();
            }
            catch (Exception exc)
            {
                _logger.Error("Error when handling IPC communication with a client.", exc);
                clientSocket?.Dispose();
            }
        }

        private void DeleteSocketFileIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exc)
            {
                _logger.Warn($"Cannot delete UNIX socket file:{path}. {exc.Message}");
            }
        }

        public void Dispose()
        {
            if (_server != null)
            {
                _server.Close();
                _server.Dispose();
            }

            DeleteSocketFileIfExists(_path);

            if (_logger.IsInfo) _logger.Info("IPC JSON RPC service stopped");
        }

        private void LogInfo(string msg)
        {
            if (_logger.IsInfo) _logger.Info(msg);
        }

        private class StateObject
        {
            public StateObject(Socket socket)
            {
                ClientSocket = socket;
            }
            public Socket ClientSocket;
            public const int BufferSize = 4096;
            public byte[] Buffer = new byte[BufferSize];

            public StringBuilder MsgBuilder = new StringBuilder();
        }
    }
}
