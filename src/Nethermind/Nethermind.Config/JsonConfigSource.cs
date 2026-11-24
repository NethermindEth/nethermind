// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Linq;

namespace Nethermind.Config;

public class JsonConfigSource : IConfigSource
{
    private const string SchemaKey = "$schema";

    public JsonConfigSource(string configFilePath) => LoadJsonConfig(configFilePath);

    public JsonConfigSource(Stream stream)
    {
        using StreamReader reader = new(stream, leaveOpen: true);
        ApplyJsonConfig(reader.ReadToEnd());
    }

    private void ApplyJsonConfig(string jsonContent)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(jsonContent);
            HashSet<string> loadedModules = [with(StringComparer.InvariantCultureIgnoreCase)];
            foreach (JsonProperty moduleEntry in json.RootElement.EnumerateObject())
            {
                if (moduleEntry.Name == SchemaKey)
                {
                    continue;
                }

                string normalizedModuleName = NormalizeModuleName(moduleEntry.Name);
                if (!loadedModules.Add(normalizedModuleName))
                {
                    throw new System.Configuration.ConfigurationErrorsException($"Duplicated config module: {moduleEntry.Name}");
                }

                LoadModule(moduleEntry.Name, moduleEntry.Value);
            }
        }
        catch (JsonException e)
        {
            throw new System.Configuration.ConfigurationErrorsException($"Config is not correctly formed JSON. See inner exception for details.", e);
        }
    }

    private void LoadJsonConfig(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            StringBuilder missingConfigFileMessage = new($"Config file {configFilePath} does not exist.");
            try
            {
                string? directory = Path.GetDirectoryName(configFilePath);
                directory = Path.IsPathRooted(configFilePath)
                    ? directory
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, directory!);

                missingConfigFileMessage.AppendLine().AppendLine($"Search directory: {directory}");

                string[] configFiles = Directory.GetFiles(directory!, "*.json");
                if (configFiles.Length > 0)
                {
                    missingConfigFileMessage.AppendLine("Found the following config files:");
                    for (int i = 0; i < configFiles.Length; i++)
                    {
                        missingConfigFileMessage.AppendLine($"  * {configFiles[i]}");
                    }
                }
            }
            catch (Exception e)
            {
                // the lines above just give extra info and config is loaded at the beginning so unlikely we have any catastrophic errors here
                missingConfigFileMessage.AppendLine($"Could not list config files: {e.Message}");
            }

            throw new IOException(missingConfigFileMessage.ToString());
        }

        ApplyJsonConfig(File.ReadAllText(configFilePath));
    }

    private void LoadModule(string moduleName, JsonElement configItems)
    {
        Dictionary<string, string> itemsDict = [with(StringComparer.InvariantCultureIgnoreCase)];

        foreach (JsonProperty configItem in configItems.EnumerateObject())
        {
            if (configItem.Name == SchemaKey)
            {
                continue;
            }

            string key = configItem.Name;
            if (!itemsDict.ContainsKey(key))
            {
                JsonElement value = configItem.Value;
                if (value.ValueKind == JsonValueKind.Number)
                {
                    itemsDict[key] = ParseNumber(value);
                }
                else if (value.ValueKind == JsonValueKind.True)
                {
                    itemsDict[key] = "true";
                }
                else if (value.ValueKind == JsonValueKind.False)
                {
                    itemsDict[key] = "false";
                }
                else if (value.ValueKind == JsonValueKind.Null)
                {
                    itemsDict[key] = "null";
                }
                else
                {
                    itemsDict[key] = configItem.Value.ToString();
                }
            }
            else
            {
                throw new System.Configuration.ConfigurationErrorsException($"Duplicated config value: {key}, module: {moduleName}");
            }
        }

        ApplyConfigValues(moduleName, itemsDict);
    }

    private readonly Dictionary<string, Dictionary<string, string>> _values = [with(StringComparer.InvariantCultureIgnoreCase)];

    private readonly Dictionary<string, Dictionary<string, object>> _parsedValues = [with(StringComparer.InvariantCultureIgnoreCase)];

    private void ApplyConfigValues(string configModule, Dictionary<string, string> items)
    {
        configModule = NormalizeModuleName(configModule);

        _values[configModule] = items;
        _parsedValues[configModule] = [with(StringComparer.InvariantCultureIgnoreCase)];
    }

    private static string NormalizeModuleName(string configModule) =>
        configModule.EndsWith("Config", StringComparison.InvariantCultureIgnoreCase) ? configModule : $"{configModule}Config";

    public (bool IsSet, object? Value) GetValue(Type type, string category, string name)
    {
        (bool isSet, string? raw) = GetRawValue(category, name);
        if (!isSet) return (false, ConfigSourceHelper.GetDefault(type));

        Dictionary<string, object> parsed = _parsedValues[category];
        if (!parsed.TryGetValue(name, out object? value))
        {
            value = parsed[name] = ConfigSourceHelper.ParseValue(type, raw!, category, name)!;
        }

        return (true, value);
    }

    public (bool IsSet, string? Value) GetRawValue(string category, string name)
    {
        if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(name))
        {
            return (false, null);
        }

        return _values.TryGetValue(category, out Dictionary<string, string>? values) && values.TryGetValue(name, out string? value)
            ? (true, value)
            : (false, null);
    }

    public IEnumerable<(string? Category, string Name)> GetConfigKeys() => _values.SelectMany(m => m.Value.Keys.Select(n => ((string?)m.Key, n)));

    private string ParseNumber(JsonElement value)
    {
        string rawText = value.GetRawText();
        if (rawText.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            return rawText;
        }
        else if (value.TryGetDouble(out double doubleResult))
        {
            return doubleResult.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            throw new System.Configuration.ConfigurationErrorsException($"Failed to parse the JSON number '{value}' as Double.");
        }
    }
}
