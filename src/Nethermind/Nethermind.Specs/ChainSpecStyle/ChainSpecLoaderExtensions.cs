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
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Specs.ChainSpecStyle
{
    public static class ChainSpecLoaderExtensions
    {

        public static ChainSpec LoadFromEmbeddedOrFile(this IChainSpecLoader chainSpecLoader, string fileName, ILogger logger)
        {
            string resourceName = fileName;
            if (!resourceName.Contains('/'))
            {
                resourceName = "chainspec/" + resourceName;
            }
            if (!resourceName.EndsWith(".json", StringComparison.InvariantCultureIgnoreCase))
            {
                resourceName += ".json";
            }
            resourceName = resourceName.Replace('/', '.');
            Assembly assembly = typeof(IConfig).Assembly;
            string[] embeddedChainSpecFiles = assembly.GetManifestResourceNames();
            if (embeddedChainSpecFiles.Any(s => s.EndsWith(resourceName)))
            {
                resourceName = "Nethermind.Config." + resourceName;
                using Stream stream = assembly.GetManifestResourceStream(resourceName);
                using StreamReader reader = new(stream);
                fileName = fileName.GetApplicationResourcePath();
                if (File.Exists(fileName))
                {
                    if (logger.IsWarn) logger.Warn("ChainSpecPath matched an embedded resource inside the binary. " +
                        "Loading chainspec from embedded resource instead file!");
                }
                return chainSpecLoader.Load(reader.ReadToEnd());
            }
            return chainSpecLoader.LoadFromFile(fileName);
        }

        public static ChainSpec LoadFromFile(this IChainSpecLoader chainSpecLoader, string filePath)
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
                    throw new Exception(missingChainspecFileMessage.ToString());
                }
            }
            
            return chainSpecLoader.Load(File.ReadAllText(filePath));
        }
    }
}
