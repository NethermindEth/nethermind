using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Cortex.BeaconNode
{
    public class BeaconNodeConfiguration
    {
        private readonly ILogger _logger;

        public BeaconNodeConfiguration(ILogger<BeaconNodeConfiguration> logger, IHostEnvironment environment)
        {
            _logger = logger;
            Version = BuildVersionString(environment.ApplicationName, environment.EnvironmentName);
        }

        public string Version { get; }

        private string BuildVersionString(string applicationName, string environmentName)
        {
            var assembley = typeof(BeaconNodeConfiguration).Assembly;
            var versionAttribute = assembley.GetCustomAttributes(false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            var version = versionAttribute.InformationalVersion;
            var versionString = $"{applicationName}/{version}";
            if (!string.IsNullOrWhiteSpace(environmentName) && environmentName != Environments.Production) 
            {
                versionString += $" ({environmentName})";
            }
            return versionString;   
        }

    }
}
