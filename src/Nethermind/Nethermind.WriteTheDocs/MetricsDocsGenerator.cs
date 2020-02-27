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
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.JsonRpc.Modules;
using Nethermind.Monitoring.Metrics;

namespace Nethermind.WriteTheDocs
{
    public class MetricsDocsGenerator : IDocsGenerator
    {
        private static List<string> _assemblyNames = new List<string>
        {
            "Nethermind.Blockchain",
            "Nethermind.Evm",
            "Nethermind.JsonRpc",
            "Nethermind.Network",
            "Nethermind.State",
        };

        public void Generate()
        {
            StringBuilder descriptionsBuilder = new StringBuilder(@"Metrics
********

Nethermind metrics can be consumed by Prometheus/Grafana if configured in Metrics configuration categoru (check configuration documentation for details). Metrics then can be used to monitor running nodes.

");

            List<Type> metricsTypes = new List<Type>();

            foreach (string assemblyName in _assemblyNames)
            {
                Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
                foreach (Type type in assembly.GetTypes().Where(t => t.Name == "Metrics"))
                {
                    metricsTypes.Add(type);
                }
            }

            foreach (Type metricsType in metricsTypes.OrderBy(t => t.FullName))
            {
                if (metricsType.FullName == null)
                {
                    // for some reasons it could be null
                    continue;
                }
                
                string metricsCategoryName = metricsType.FullName.Replace("Nethermind.", "").Replace(".Metrics", "");
                descriptionsBuilder.Append($@"
{metricsCategoryName}
{string.Empty.PadLeft(metricsCategoryName.Length, '^')}

");
                
                var properties = metricsType.GetProperties(BindingFlags.Public | BindingFlags.Static);
                foreach (PropertyInfo methodInfo in properties.OrderBy(p => p.Name))
                {
                    DescriptionAttribute attribute = methodInfo.GetCustomAttribute<DescriptionAttribute>();
                    string description = attribute == null ? "<missing description>" : attribute.Description;
                    descriptionsBuilder.AppendLine(@$"
 nethermind_{MetricsUpdater.BuildGaugeName(methodInfo.Name)}
  {description}
");
                }
            }

            string result = descriptionsBuilder.ToString();

            Console.WriteLine(result);
            File.WriteAllText("metrics.rst", result);
            string sourceDir = DocsDirFinder.FindDocsDir();
            File.WriteAllText(Path.Combine(sourceDir, "metrics.rst"), result);
        }
    }
}