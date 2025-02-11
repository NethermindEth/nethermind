// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle;

public class ChainSpecFileLoader
{
    private readonly Dictionary<string, IChainSpecLoader> _chainSpecLoaders;
    private readonly ILogger _logger;

    public ChainSpecFileLoader(IJsonSerializer serializer, ILogger logger)
    {
        var jsonLoader = new ChainSpecLoader(serializer);
        _chainSpecLoaders = new Dictionary<string, IChainSpecLoader>
        {
            { ".json", jsonLoader },
            { ".zst", new ZstdChainSpecLoader(jsonLoader) }
        };
        _logger = logger;
    }

    public ChainSpec LoadEmbeddedOrFromFile(string fileName)
    {
        fileName = NormalizeFileName(fileName);
        var extension = Path.GetExtension(fileName);

        string resourceName = FileNameToResource(fileName);
        Assembly assembly = typeof(IConfig).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            if (_logger.IsInfo) _logger.Info("Loading ChainSpec from embedded resources");
            return _chainSpecLoaders[extension].Load(stream);
        }
        else
        {
            if (_logger.IsInfo) _logger.Info($"Did not find ChainSpec in embedded resources: {fileName}");
            return LoadFromFile(fileName);
        }
    }

    private static string FileNameToResource(string fileName)
    {
        var sb = new StringBuilder();
        sb.Append("Nethermind.Config.");
        if (!fileName.Contains('/'))
        {
            sb.Append("chainspec/");
        }

        sb.Append(fileName);
        sb.Replace('/', '.');
        return sb.ToString();
    }

    private static string NormalizeFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension == "" ? $"{fileName}.json" : fileName;
    }

    private ChainSpec LoadFromFile(string filePath)
    {
        filePath = CheckEmbeddedChainSpec(filePath);
        string extension = Path.GetExtension(filePath);
        if (_logger.IsInfo) _logger.Info($"Loading ChainSpec from file: {filePath}");
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return _chainSpecLoaders[extension].Load(fileStream);
    }

    private static string CheckEmbeddedChainSpec(string filePath)
    {
        filePath = filePath.GetApplicationResourcePath();
        if (!File.Exists(filePath))
        {
            StringBuilder missingChainspecFileMessage = new($"ChainSpec file does not exist {filePath}");
            try
            {
                missingChainspecFileMessage.AppendLine().AppendLine("Did you mean any of these:");
                string[] jsonFiles = Directory.GetFiles(Path.GetDirectoryName(filePath), "*.json");
                string[] zstdFiles = Directory.GetFiles(Path.GetDirectoryName(filePath), "*.zst");

                var configFiles = Enumerable.Empty<string>().Concat(jsonFiles).Concat(zstdFiles);
                foreach (var configFile in configFiles)
                {
                    missingChainspecFileMessage.AppendLine($"  * {configFile}");
                }
            }
            catch (Exception e)
            {
                throw new FileNotFoundException(missingChainspecFileMessage.ToString(), e);
            }
            throw new FileNotFoundException(missingChainspecFileMessage.ToString());
        }

        return filePath;
    }
}
