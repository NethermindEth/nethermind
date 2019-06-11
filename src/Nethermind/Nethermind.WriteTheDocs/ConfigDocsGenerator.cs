/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
            "Nethermind.Clique",
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

");

            StringBuilder exampleBuilder = new StringBuilder(@"Sample configuration (mainnet)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

::

    [
");

            List<Type> configTypes = new List<Type>();

            foreach (string assemblyName in _assemblyNames)
            {
                Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
                foreach (Type type in assembly.GetTypes().Where(t => typeof(IConfig).IsAssignableFrom(t)).Where(t => !t.IsInterface))
                {
                    configTypes.Add(type);
                }
            }

            foreach (Type configType in configTypes.OrderBy(t => t.Name))
            {
                descriptionsBuilder.Append($@"{configType.Name}
{string.Empty.PadLeft(configType.Name.Length, '^')}

");

                exampleBuilder.AppendLine("      {");
                exampleBuilder.AppendLine($"        \"ConfigModule\": \"{configType.Name}\"");
                exampleBuilder.AppendLine("        \"ConfigItems\": {");

                var properties = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (PropertyInfo propertyInfo in properties.OrderBy(p => p.Name))
                {
                    exampleBuilder.AppendLine($"          \"{propertyInfo.Name}\" : example");
                    ConfigItemAttribute attribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>();
                    if (attribute == null)
                    {
                        descriptionsBuilder.AppendLine($" - {propertyInfo.Name} - description missing").AppendLine();
                        continue;
                    }

                    descriptionsBuilder.AppendLine($" - {propertyInfo.Name} - {attribute.Description}").AppendLine();
                }

                exampleBuilder.AppendLine("        }");
                exampleBuilder.AppendLine("      },");
            }

            exampleBuilder.AppendLine("    ]");

            string result = string.Concat(descriptionsBuilder.ToString(), exampleBuilder.ToString());

            Console.WriteLine(result);
            File.WriteAllText("configuration.rst", result);
        }
    }
}