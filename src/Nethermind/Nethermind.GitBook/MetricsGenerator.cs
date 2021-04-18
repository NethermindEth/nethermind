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
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Nethermind.GitBook
{
    public class MetricsGenerator
    {
        private readonly SharedContent _sharedContent;
        
        public MetricsGenerator(SharedContent sharedContent)
        {
            _sharedContent = sharedContent;
        }
        
        public void Generate()
        {
            string docsDir = DocsDirFinder.FindDocsDir();
            
            string[] dlls = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll")
                .OrderBy(n => n).ToArray();

            foreach (string dll in dlls)
            {
                Assembly assembly = Assembly.LoadFile(dll);
                Type[] modules  = assembly.GetExportedTypes().Where(t => t.Name == "Metrics").ToArray();
                
                foreach (Type module in modules)
                {
                    GenerateDocFileContent(module, docsDir);
                }
            }
        }

        private void GenerateDocFileContent(Type metricsType, string docsDir)
        {
            StringBuilder docBuilder = new StringBuilder();

            PropertyInfo[] moduleProperties = metricsType.GetProperties().OrderBy(x => x.Name).ToArray();
            
            string moduleName = metricsType.FullName.Replace("Nethermind.", "").Replace(".Metrics", "");

            docBuilder.AppendLine(@$"# {moduleName}");
            docBuilder.AppendLine();
            moduleName = moduleName.ToLower();
            docBuilder.AppendLine("| Metric | Description |");
            docBuilder.AppendLine("| :--- | :--- |");
            
            if (moduleProperties.Length == 0) return;
            
            foreach (PropertyInfo property in moduleProperties)
            {
                Attribute attr = property.GetCustomAttribute(typeof(DescriptionAttribute));
                docBuilder.AppendLine($"| {GetMetricName(property.Name)} | {((DescriptionAttribute)attr)?.Description ?? ""} |");
            }
            _sharedContent.Save(moduleName, docsDir + "/ethereum-client/metrics", docBuilder);
        }

        private string GetMetricName(string propertyName)
        {
            StringBuilder nameBuilder = new StringBuilder("nethermind");
            
            foreach (char ch in propertyName)
            {
                if (char.IsUpper(ch))
                {
                    nameBuilder.Append("_");
                    nameBuilder.Append(ch.ToString().ToLower());
                }
                else
                {
                    nameBuilder.Append(ch);
                }
            }

            return nameBuilder.ToString();
        }
    }
}
