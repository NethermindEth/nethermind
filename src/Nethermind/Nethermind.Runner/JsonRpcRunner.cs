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
using System.Linq;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.DataModel;
using Unity;

namespace Nethermind.Runner
{
    public class JsonRpcRunner : IJsonRpcRunner
    {
        private readonly ILogger _logger;
        //TODO: replace with something supported by .NET Core
//        private UnityServiceHost _serviceHost;
        private readonly IConfigurationProvider _configurationProvider;

        public IUnityContainer Container { private get; set; }

        public JsonRpcRunner(IConfigurationProvider configurationProvider, ILogger logger)
        {
            _configurationProvider = configurationProvider;
            _logger = logger;
        }

        public void Start(IEnumerable<ModuleType> modules = null)
        {
            if (modules != null && modules.Any())
            {
                _configurationProvider.EnabledModules = modules;
            }

            _logger.Log($"Starting http service, modules: {string.Join(", ", _configurationProvider.EnabledModules.Select(x => x))}");

            //TODO: replace with something supported by .NET Core
//            _serviceHost = new UnityServiceHost(Container, typeof(JsonRpcService));
//            _serviceHost.Open();

            //TODO: replace with something supported by .NET Core
//            foreach (var endpoint in _serviceHost.Description.Endpoints)
//            {
//                _logger.Log($"Opened service: {endpoint.Address}");
//            }
        }

        public void Stop(IEnumerable<ModuleType> modules = null)
        {
            try
            {
                //TODO: replace with something supported by .NET Core
//                if (_serviceHost != null && _serviceHost.State != CommunicationState.Closed)
//                {
//                    _serviceHost.Close();
//                }
                _logger.Log("Service stopped");
            }
            catch (Exception e)
            {
                _logger.Log($"Error during stopping service: {e}");
            }
        }
    }
}