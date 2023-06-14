// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Jint.Native;
using Jint.Native.Json;
using Nethermind.Cli.Console;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Cli
{
    public class NodeManager : INodeManager
    {
        private readonly ILogManager _logManager;

        private readonly IJsonSerializer _serializer;

        private readonly ICliConsole _cliConsole;

        private readonly JsonParser _jsonParser;

        private readonly Dictionary<Uri, IJsonRpcClient> _clients = new();

        private IJsonRpcClient? _currentClient;

        public NodeManager(ICliEngine cliEngine, IJsonSerializer serializer, ICliConsole cliConsole, ILogManager logManager)
        {
            ICliEngine cliEngine1 = cliEngine ?? throw new ArgumentNullException(nameof(cliEngine));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _cliConsole = cliConsole ?? throw new ArgumentNullException(nameof(cliConsole));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            _jsonParser = new JsonParser(cliEngine1.JintEngine);
        }

        public string? CurrentUri { get; private set; }

        public void SwitchUri(Uri uri)
        {
            CurrentUri = uri.ToString();
            if (!_clients.ContainsKey(uri))
            {
                _clients[uri] = new BasicJsonRpcClient(uri, _serializer, _logManager);
            }

            _currentClient = _clients[uri];
        }

        public void SwitchClient(IJsonRpcClient client)
        {
            _currentClient = client;
        }

        public async Task<JsValue> PostJint(string method, params object[] parameters)
        {
            try
            {
                if (_currentClient is null)
                {
                    //_cliConsole.WriteErrorLine("[INTERNAL ERROR] JSON RPC client not set.");
                }
                else
                {
                    Stopwatch stopwatch = new();
                    stopwatch.Start();
                    object? result = await _currentClient.Post<object>(method, parameters);
                    stopwatch.Stop();
                    decimal totalMicroseconds = stopwatch.ElapsedTicks * (1_000_000m / Stopwatch.Frequency);
                    Colorful.Console.WriteLine($"Request complete in {totalMicroseconds}μs");

                    if (result is bool boolResult)
                    {
                        return boolResult ? JsValue.True : JsValue.False;
                    }

                    string? resultString = result?.ToString();
                    if (resultString != "0x" && resultString is not null)
                    {
                        return _jsonParser.Parse(resultString);
                    }
                }
            }
            catch (HttpRequestException e)
            {
                _cliConsole.WriteErrorLine("  " + e.Message);
                _cliConsole.Write("  Use ");
                _cliConsole.WriteKeyword("node");
                _cliConsole.WriteLine(".switch(\"ip:port\") to change the target machine");

                _cliConsole.WriteLine("  Make sure that JSON RPC is enabled on the target machine (--JsonRpc.Enabled true)");
                _cliConsole.WriteLine("  Make sure that firewall is open for the JSON RPC port on the target machine");
            }
            catch (Exception e)
            {
                _cliConsole.WriteException(e);
            }
            return JsValue.Null;
        }

        public async Task<string?> Post(string method, params object?[] parameters)
        {
            return await Post<string>(method, parameters);
        }

        public async Task<T?> Post<T>(string method, params object?[] parameters)
        {
            T? result = default;
            try
            {
                if (_currentClient is null)
                {
                    //_cliConsole.WriteErrorLine("[INTERNAL ERROR] JSON RPC client not set.");
                }
                else
                {
                    Stopwatch stopwatch = new();
                    stopwatch.Start();
                    result = await _currentClient.Post<T>(method, parameters);
                    stopwatch.Stop();
                    decimal totalMicroseconds = stopwatch.ElapsedTicks * (1_000_000m / Stopwatch.Frequency);
                    Colorful.Console.WriteLine($"Request complete in {totalMicroseconds}μs");
                }
            }
            catch (HttpRequestException e)
            {
                _cliConsole.WriteErrorLine("  " + e.Message);
                _cliConsole.Write("  Use ");
                _cliConsole.WriteKeyword("node");
                _cliConsole.WriteLine(".switch(\"ip:port\") to change the target machine");

                _cliConsole.WriteLine("  Make sure that JSON RPC is enabled on the target machine (--JsonRpc.Enabled true)");
                _cliConsole.WriteLine("  Make sure that firewall is open for the JSON RPC port on the target machine");
            }
            catch (Exception e)
            {
                _cliConsole.WriteException(e);
            }

#pragma warning disable 8603
            return result;
#pragma warning restore 8603
        }
    }
}
