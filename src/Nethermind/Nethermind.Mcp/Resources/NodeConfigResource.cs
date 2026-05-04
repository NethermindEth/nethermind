// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Resources;

[McpServerResourceType]
public sealed class NodeConfigResource(IConfigProvider configProvider, ConfigRedactor redactor)
{
    [McpServerResource(UriTemplate = "nethermind://node/config"), Description("Sanitized node configuration with sensitive values redacted.")]
    public NodeConfigDto Read()
    {
        Dictionary<string, IReadOnlyDictionary<string, object?>> sections = new();
        foreach ((Type configType, object instance) in EnumerateConfigs(configProvider))
        {
            // Strip the leading "I" from the interface name and the trailing "Config" suffix:
            // "IJsonRpcConfig" -> "JsonRpc"
            string sectionName = configType.Name;
            if (sectionName.StartsWith('I')) sectionName = sectionName[1..];
            sectionName = sectionName.RemoveEnd("Config");
            sections[sectionName] = ToDictionary(instance);
        }
        return new NodeConfigDto(redactor.Redact(sections));
    }

    private static IEnumerable<(Type ConfigType, object Instance)> EnumerateConfigs(IConfigProvider provider)
    {
        // Discover IConfig-derived interfaces across loaded Nethermind assemblies and ask the provider
        // for each instance. This mirrors how ConfigProvider.Initialize / ConfigFileTestsBase enumerates configs.
        IEnumerable<Type> configInterfaces = TypeDiscovery
            .FindNethermindBasedTypes(typeof(IConfig))
            .Where(static t => t.IsInterface && t != typeof(INoCategoryConfig));

        foreach (Type configType in configInterfaces)
        {
            IConfig instance;
            try
            {
                instance = provider.GetConfig(configType);
            }
            catch (Exception)
            {
                // Skip config types the provider cannot satisfy (e.g. no direct implementation).
                continue;
            }
            yield return (configType, instance);
        }
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(object instance)
    {
        Type type = instance.GetType();
        Dictionary<string, object?> dict = new();
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            if (!property.CanRead) continue;

            object? value;
            try
            {
                value = property.GetValue(instance);
            }
            catch (Exception ex)
            {
                value = $"<error: {ex.GetType().Name}>";
            }
            dict[property.Name] = value;
        }
        return dict;
    }
}
