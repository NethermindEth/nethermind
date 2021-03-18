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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.Config;

namespace Nethermind.GitBook
{
    public class ConfigGenerator
    {
        private readonly SharedContent _sharedContent;
        
        public ConfigGenerator(SharedContent sharedContent)
        {
            _sharedContent = sharedContent;
        }
        
        public void Generate()
        {
            string docsDir = DocsDirFinder.FindDocsDir();
            List<Type> configTypes = GetConfigModules();
        
            foreach (Type configType in configTypes)
            {
                
                GenerateDocFileContent(configType, docsDir);
            }
        }
        
        private List<Type> GetConfigModules()
        {
            IEnumerable<Assembly> nethermindAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().ToString().StartsWith("Nethermind"));
            
            List<Type> configModules = new List<Type>();
            
            foreach (Assembly assembly in nethermindAssemblies)
            {
                foreach (Type type in assembly.GetTypes()
                    .Where(t => typeof(IConfig).IsAssignableFrom(t))
                    .Where(t => t.IsInterface && t != typeof(IConfig)))
                {
                    configModules.Add(type);
                }
            }

            // ToFix: algorithm above is not creating .md files for some modules. Below added manually
             configModules.Add(Assembly.Load("Nethermind.EthStats").GetTypes()
                 .Where(t => typeof(IConfig).IsAssignableFrom(t))
                 .First(t => t.IsInterface && t != typeof(IConfig)));
             
             configModules.Add(Assembly.Load("Nethermind.HealthChecks").GetTypes()
                 .Where(t => typeof(IConfig).IsAssignableFrom(t))
                 .First(t => t.IsInterface && t != typeof(IConfig)));
             
             configModules.Add(Assembly.Load("Nethermind.Seq").GetTypes()
                 .Where(t => typeof(IConfig).IsAssignableFrom(t))
                 .First(t => t.IsInterface && t != typeof(IConfig)));
            
            return configModules;
        }
        
        private void GenerateDocFileContent(Type configType, string docsDir)
        {
            Attribute attribute = configType.GetCustomAttribute(typeof(ConfigCategoryAttribute));
            if(((ConfigCategoryAttribute)attribute)?.HiddenFromDocs ?? false) return;
            
            StringBuilder docBuilder = new StringBuilder();
            string moduleName = configType.Name.Substring(1).Replace("Config", "");

            PropertyInfo[] moduleProperties = configType.GetProperties().OrderBy(p => p.Name).ToArray();

            docBuilder.AppendLine(@$"# {moduleName}");
            moduleName = moduleName.ToLower();
            docBuilder.AppendLine();
            docBuilder.AppendLine($"{((ConfigCategoryAttribute)attribute)?.Description ?? ""}");
            docBuilder.AppendLine();
            docBuilder.AppendLine("| Property | Description | Default |");
            docBuilder.AppendLine("| :--- | :--- | :--- |");

            if (moduleProperties.Length == 0) return;
            
            foreach (PropertyInfo property in moduleProperties)
            {
                Attribute attr = property.GetCustomAttribute(typeof(ConfigItemAttribute));
                if(((ConfigItemAttribute)attr)?.HiddenFromDocs ?? false) continue;
                docBuilder.AppendLine($"| {property.Name} | {((ConfigItemAttribute)attr)?.Description ?? ""} | {((ConfigItemAttribute)attr)?.DefaultValue ?? ""} |");
            }

            string path = string.Concat(docsDir, "/ethereum-client/configuration");
            _sharedContent.Save(moduleName, path, docBuilder);
        }
    }
}
