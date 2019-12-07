using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nethermind.BeaconNode
{
    public class BeaconNodeConfiguration
    {
        private const string ProductToken = "Cortex";
        private readonly ILogger _logger;

        public BeaconNodeConfiguration(ILogger<BeaconNodeConfiguration> logger, IHostEnvironment environment)
        {
            _logger = logger;
            Version = BuildVersionString(ProductToken, environment.EnvironmentName);
        }

        public string Version { get; }

        private string BuildVersionString(string productToken, string environmentName)
        {
            var parts = new List<string>();

            var assembly = typeof(BeaconNodeConfiguration).Assembly;
            var versionAttribute = assembly.GetCustomAttributes(false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            var version = versionAttribute.InformationalVersion;
            var product1 = $"{productToken}/{version}";
            parts.Add(product1);

            if (!string.IsNullOrWhiteSpace(environmentName) && environmentName != Environments.Production)
            {
                var comment1 = $"({environmentName})";
                parts.Add(comment1);
            }

            var versionString = string.Join(" ", parts);
            return versionString;
        }
    }
}
