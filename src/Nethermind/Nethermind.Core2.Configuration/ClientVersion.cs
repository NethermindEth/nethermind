// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nethermind.Core2.Configuration
{
    public class ClientVersion : IClientVersion
    {
        private readonly ILogger _logger;

        public ClientVersion(ILogger<ClientVersion> logger, IHostEnvironment environment)
        {
            _logger = logger;
            Description = BuildVersionDescription(environment.EnvironmentName);
        }

        /// <summary>
        /// Product, version, platform, and environment details to identify the application.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Format similar to a  [HTTP User-Agent](https://tools.ietf.org/html/rfc7231#section-5.5.3) field.
        /// </para>
        /// <para>
        /// This consists of one or more product identifiers, each followed by zero or more comments.
        /// Each product identifier consists of a name and optional version (separated by a slash).
        /// </para>
        /// <para>
        /// By convention, the product identifiers are listed in decreasing order of their significance for identifying the software.
        /// Commonly there may be only one product.
        /// </para>
        /// <para>
        /// Comments are enclosed in parentheses, [Section 3.2 of RFC 7230](https://tools.ietf.org/html/rfc7230#section-3.2.6).
        /// </para>
        /// </remarks>
        public string Description { get; }

        private string BuildVersionDescription(string environmentName)
        {
            List<string> parts = new List<string>();

            Assembly assembly = typeof(ClientVersion).Assembly;

            AssemblyProductAttribute productAttribute =
                assembly.GetCustomAttributes(false).OfType<AssemblyProductAttribute>().FirstOrDefault();
            string productToken = productAttribute.Product;

            AssemblyInformationalVersionAttribute versionAttribute = assembly.GetCustomAttributes(false)
                .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            string version = versionAttribute.InformationalVersion;
            string product1 = $"{productToken}/{version}";
            parts.Add(product1);

            Architecture osArchitecture = RuntimeInformation.OSArchitecture;
            string osDescription = RuntimeInformation.OSDescription;
            if (osDescription.Contains('#'))
            {
                int indexOfHash = osDescription.IndexOf('#');
                osDescription = osDescription.Substring(0, Math.Max(0, indexOfHash - 1));
            }

            string frameworkDescription = RuntimeInformation.FrameworkDescription;
            string osFrameworkComment = $"({osArchitecture}-{osDescription}/{frameworkDescription})";
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
