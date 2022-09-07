//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Primitives;
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
            using StreamReader reader = new(stream);
            fileName = fileName.GetApplicationResourcePath();
            if (File.Exists(fileName))
            {
                if (logger.IsWarn) logger.Warn("ChainSpecPath matched an embedded resource inside the binary. Loading chainspec from embedded resources instead of file!");
            }
            else
            {
                if (logger.IsInfo) logger.Info($"Loading chainspec from embedded resources: {fileName}");
            }
            return chainSpecLoader.Load(reader.ReadToEnd());
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

    public static ChainSpec LoadFromFile(this IChainSpecLoader chainSpecLoader, string filePath, ILogger logger)
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
            finally
            {
                throw new FileNotFoundException(missingChainspecFileMessage.ToString());
            }
        }
        if (logger.IsInfo) logger.Info($"Loading chainspec from file: {filePath}");
        return chainSpecLoader.Load(File.ReadAllText(filePath));
    }
}
