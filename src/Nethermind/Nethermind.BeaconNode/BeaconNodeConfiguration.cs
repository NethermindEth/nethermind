using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nethermind.BeaconNode
{
    public class BeaconNodeConfiguration
    {
        private const string ProductToken = "Nethermind";
        private readonly ILogger _logger;

        public BeaconNodeConfiguration(ILogger<BeaconNodeConfiguration> logger, IHostEnvironment environment)
        {
            _logger = logger;
            Version = BuildVersionString(ProductToken, environment.EnvironmentName);
        }

        public string Version { get; }

        private string BuildVersionString(string productToken, string environmentName)
        {
            List<string> parts = new List<string>();

            Assembly assembly = typeof(BeaconNodeConfiguration).Assembly;
            AssemblyInformationalVersionAttribute versionAttribute = assembly.GetCustomAttributes(false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            string version = versionAttribute.InformationalVersion;
            string product1 = $"{productToken}/{version}";
            parts.Add(product1);

            Architecture osArchiteture = RuntimeInformation.OSArchitecture;
            string osDescription = RuntimeInformation.OSDescription;
            if (osDescription.Contains('#'))
            {
                int indexOfHash = osDescription.IndexOf('#');
                osDescription = osDescription.Substring(0, Math.Max(0, indexOfHash - 1));
            }
            string frameworkDescription = RuntimeInformation.FrameworkDescription;
            string osFrameworkComment = $"({osArchiteture}-{osDescription}/{frameworkDescription})";
            parts.Add(osFrameworkComment);

            if (!string.IsNullOrWhiteSpace(environmentName) && environmentName != Environments.Production)
            {
                string environmentComment = $"({environmentName})";
                parts.Add(environmentComment);
            }

            string versionString = string.Join(" ", parts);
            return versionString;
        }
    }
}
