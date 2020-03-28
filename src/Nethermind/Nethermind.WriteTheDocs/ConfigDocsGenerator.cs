//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.Config;

namespace Nethermind.WriteTheDocs
{
    public class ConfigDocsGenerator : IDocsGenerator
    {
        private static List<string> _assemblyNames = new List<string>
        {
            "Nethermind.Blockchain",
            "Nethermind.Consensus.Clique",
            "Nethermind.Db",
            "Nethermind.EthStats",
            "Nethermind.JsonRpc",
            "Nethermind.KeyStore",
            "Nethermind.Monitoring",
            "Nethermind.Network",
            "Nethermind.Runner",
        };

        public void Generate()
        {
            StringBuilder descriptionsBuilder = new StringBuilder(@"Configuration
*************

Use '/' as the path separator so the configs can be shared between all platforms supported (Linux, Windows, MacOS).
'--config', '--baseDbPath', and '--log' options are available from the command line to select config file, base DB directory prefix and log level respectively. 

");

            StringBuilder exampleBuilder = new StringBuilder(@"Sample configuration (mainnet)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

::

    {
");

            List<(Type ConfigType, Type ConfigInterface)> configTypes = new List<(Type, Type)>();

            foreach (string assemblyName in _assemblyNames)
            {
                Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
                foreach (Type type in assembly.GetTypes().Where(t => typeof(IConfig).IsAssignableFrom(t)).Where(t => !t.IsInterface))
                {
                    var configInterface = type.GetInterfaces().Single(i => i != typeof(IConfig));
                    configTypes.Add((type, configInterface));
                }
            }

            foreach ((Type configType, Type configInterface) in configTypes.OrderBy(t => t.ConfigType.Name))
            {
                descriptionsBuilder.Append($@"{configType.Name}
{string.Empty.PadLeft(configType.Name.Length, '^')}

");

                ConfigCategoryAttribute categoryAttribute = configInterface.GetCustomAttribute<ConfigCategoryAttribute>();
                if (categoryAttribute != null)
                {
                    descriptionsBuilder.AppendLine($"{categoryAttribute.Description}").AppendLine();
                }
                
                exampleBuilder.AppendLine($"        \"{configType.Name.Replace("Config", string.Empty)}\": {{");

                var properties = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                int propertyIndex = 0;
                foreach (PropertyInfo propertyInfo in properties.OrderBy(p => p.Name))
                {
                    propertyIndex++;
                    PropertyInfo interfaceProperty = configInterface.GetProperty(propertyInfo.Name);
                    if (interfaceProperty == null)
                    {
                        if (propertyInfo.GetCustomAttribute<ObsoleteAttribute>() == null)
                        {
                            Console.WriteLine($"Property {propertyInfo.Name} is missing from interface {configInterface.Name}.");
                        }
                    }
                    else
                    {
                        ConfigItemAttribute attribute = interfaceProperty.GetCustomAttribute<ConfigItemAttribute>();
                        string defaultValue = attribute == null ? "[MISSING_DOCS]" : attribute.DefaultValue;

                        if (propertyIndex == properties.Length)
                        {
                            exampleBuilder.AppendLine($"              \"{propertyInfo.Name}\" : {defaultValue}");
                        }
                        else
                        {
                            exampleBuilder.AppendLine($"              \"{propertyInfo.Name}\" : {defaultValue},");
                        }

                        if (attribute == null)
                        {
                            descriptionsBuilder.AppendLine($" {propertyInfo.Name}").AppendLine();
                            continue;
                        }

                        descriptionsBuilder
                            .AppendLine($" {propertyInfo.Name}")
                            .AppendLine($"   {attribute.Description}")
                            .AppendLine($"   default value: {defaultValue}")
                            .AppendLine();
                    }
                }

                exampleBuilder.AppendLine("        },");
            }

            exampleBuilder.AppendLine("    }");

            string result = string.Concat(descriptionsBuilder.ToString(), exampleBuilder.ToString());

            Console.WriteLine(result);
            File.WriteAllText("configuration.rst", result);
            string sourceDir = DocsDirFinder.FindDocsDir();
            File.WriteAllText(Path.Combine(sourceDir, "configuration.rst"), result);
        }
    }
}