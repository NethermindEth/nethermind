// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Text;
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Specs.ChainSpecStyle;

public static class ChainSpecLoaderExtensions
{

    public static ChainSpec LoadEmbeddedOrFromFile(this IChainSpecLoader chainSpecLoader, string fileName, ILogger logger)
    {
        try
        {
            string resourceName = GetResourceName(fileName);
            Assembly assembly = typeof(IConfig).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                if (logger.IsInfo) logger.Info($"Did not find chainspec in embedded resources: {fileName}");
                return chainSpecLoader.LoadFromFile(fileName, logger);
            }
            fileName = fileName.GetApplicationResourcePath();
            if (File.Exists(fileName))
            {
                if (logger.IsInfo) logger.Info("ChainSpecPath matched an embedded resource inside the binary. Loading chainspec from embedded resources instead of file!");
            }
            else
            {
                if (logger.IsInfo) logger.Info($"Loading chainspec from embedded resources: {fileName}");
            }
            return chainSpecLoader.Load(stream);
        }
        catch (Exception ex)
        {
            if (logger.IsError) logger.Error("Error while loading Chainspec. Falling back to loading from file.", ex);
            return chainSpecLoader.LoadFromFile(fileName, logger);
        }
    }

    private static string GetResourceName(string fileName)
    {
        StringBuilder builder = new();
        builder.Append("Nethermind.Config.");
        if (!fileName.Contains('/'))
        {
            builder.Append("chainspec/");
        }
        builder.Append(fileName);
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(".json");
        }
        builder.Replace('/', '.');
        return builder.ToString();
    }

    public static ChainSpec LoadFromFile(this IChainSpecLoader chainSpecLoader, string filePath, ILogger? logger = null)
    {
        filePath = CheckEmbeddedChainSpec(filePath);
        if (logger is null) return LoadFromFileInternal(chainSpecLoader, filePath);

        ILogger log = logger.Value;
        if (log.IsInfo) log.Info($"Loading chainspec from file: {filePath}");
        return LoadFromFileInternal(chainSpecLoader, filePath);
    }

    private static ChainSpec LoadFromFileInternal(IChainSpecLoader chainSpecLoader, string filePath)
    {
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return chainSpecLoader.Load(fileStream);
    }

    private static string CheckEmbeddedChainSpec(string filePath)
    {
        filePath = filePath.GetApplicationResourcePath();
        if (!File.Exists(filePath))
        {
            StringBuilder missingChainspecFileMessage = new($"Chainspec file does not exist {filePath}");
            try
            {
                missingChainspecFileMessage.AppendLine().AppendLine("Did you mean any of these:");
                string[] configFiles = Directory.GetFiles(Path.GetDirectoryName(filePath), "*.json");
                for (int i = 0; i < configFiles.Length; i++)
                {
                    missingChainspecFileMessage.AppendLine($"  * {configFiles[i]}");
                }
            }
            catch (Exception)
            {
                // do nothing - the lines above just give extra info and config is loaded at the beginning so unlikely we have any catastrophic errors here
            }

            throw new FileNotFoundException(missingChainspecFileMessage.ToString());
        }

        return filePath;
    }
}
