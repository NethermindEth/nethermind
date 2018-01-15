using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Nevermind.Core;
using Nevermind.JsonRpc;
using Nevermind.JsonRpc.DataModel;
using Unity;
using Unity.Wcf;

namespace Nevermind.Runner
{
    public class JsonRpcRunner : IJsonRpcRunner
    {
        private readonly ILogger _logger;
        private UnityServiceHost _serviceHost;
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

            _serviceHost = new UnityServiceHost(Container, typeof(JsonRpcService));
            _serviceHost.Open();

            foreach (var endpoint in _serviceHost.Description.Endpoints)
            {
                _logger.Log($"Opened service: {endpoint.Address}");
            }
        }

        public void Stop(IEnumerable<ModuleType> modules = null)
        {
            try
            {
                if (_serviceHost != null && _serviceHost.State != CommunicationState.Closed)
                {
                    _serviceHost.Close();
                }
                _logger.Log("Service stopped");
            }
            catch (Exception e)
            {
                _logger.Log($"Error during stopping service: {e}");
            }
        }
    }
}