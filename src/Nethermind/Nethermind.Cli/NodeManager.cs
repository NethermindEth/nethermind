/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Nethermind.Core;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;

namespace Nethermind.Cli
{
    public class NodeManager : INodeManager
    {
        private ICliEngine _cliEngine;
        private ILogManager _logManager;
        private IJsonSerializer _serializer;
        private JsonParser _jsonParser;

        private Dictionary<Uri, IJsonRpcClient> _clients = new Dictionary<Uri, IJsonRpcClient>();

        private IJsonRpcClient _currentClient;

        public NodeManager(ICliEngine cliEngine, IJsonSerializer serializer, ILogManager logManager)
        {
            _cliEngine = cliEngine ?? throw new ArgumentNullException(nameof(cliEngine));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            _jsonParser = new JsonParser(_cliEngine.JintEngine);
        }

        public string CurrentUri { get; private set; }

        public void SwitchUri(Uri uri)
        {
            CurrentUri = uri.ToString();
            if (!_clients.ContainsKey(uri))
            {
                _clients[uri] = new BasicJsonRpcClient(uri, _serializer, _logManager);
            }

            _currentClient = _clients[uri];
        }

        public async Task<JsValue> PostJint(string method, params object[] parameters)
        {
            try
            {
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                object result = await _currentClient.Post<object>(method, parameters);
                stopwatch.Stop();
                decimal totalMicroseconds = stopwatch.ElapsedTicks * (1_000_000m / Stopwatch.Frequency);
                Console.WriteLine($"Request complete in {totalMicroseconds}μs");
                string resultString = result?.ToString();
                if (resultString == "0x")
                {
                    return JsValue.Null;
                }

                return resultString == null ? JsValue.Null : _jsonParser.Parse(resultString);
            }
            catch (HttpRequestException e)
            {
                CliConsole.WriteErrorLine(e.Message);
            }
            catch (Exception e)
            {
                CliConsole.WriteException(e);
            }

            return JsValue.Null;
        }

        public async Task<string> Post(string method, params object[] parameters)
        {
            return await Post<string>(method, parameters);
        }

        public async Task<T> Post<T>(string method, params object[] parameters)
        {
            try
            {
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                T result = await _currentClient.Post<T>(method, parameters);
                stopwatch.Stop();
                decimal totalMicroseconds = stopwatch.ElapsedTicks * (1_000_000m / Stopwatch.Frequency);
                Console.WriteLine($"Request complete in {totalMicroseconds}μs");
                return result;
            }
            catch (HttpRequestException e)
            {
                CliConsole.WriteErrorLine(e.Message);
            }
            catch (Exception e)
            {
                CliConsole.WriteException(e);
            }

            return default;
        }
    }
}