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
            string docsDir = DocsDirFinder.FindConfig();
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

            // ToFix: algorithm above is not creating .md file for IEthStatsConfig. Below added manually
             configModules.Add(Assembly.Load("Nethermind.EthStats").GetTypes()
                 .Where(t => typeof(IConfig).IsAssignableFrom(t))
                 .First(t => t.IsInterface && t != typeof(IConfig)));
            
            return configModules;
        }
        
        private void GenerateDocFileContent(Type metricsType, string docsDir)
        {
            Attribute attribute = metricsType.GetCustomAttribute(typeof(ConfigCategoryAttribute));
            if(((ConfigCategoryAttribute)attribute)?.HiddenFromDocs ?? false) return;
            
            StringBuilder docBuilder = new StringBuilder();
            string moduleName = metricsType.Name.Substring(1).Replace("Config", "");

            PropertyInfo[] moduleProperties = metricsType.GetProperties().OrderBy(p => p.Name).ToArray();

            docBuilder.AppendLine(@$"# {moduleName}");
            moduleName = moduleName.ToLower();
            docBuilder.AppendLine();
            docBuilder.AppendLine($"{((ConfigCategoryAttribute)attribute)?.Description ?? ""}");
            docBuilder.AppendLine();
            docBuilder.AppendLine("| Metric Name | Description | Default |");
            docBuilder.AppendLine("| :--- | :--- | ---: |");

            if (moduleProperties.Length == 0) return;
            
            foreach (PropertyInfo property in moduleProperties)
            {
                Attribute attr = property.GetCustomAttribute(typeof(ConfigItemAttribute));
                if(((ConfigItemAttribute)attr)?.HiddenFromDocs ?? false) continue;
                docBuilder.AppendLine($"| {property.Name} | {((ConfigItemAttribute)attr)?.Description ?? ""} | {((ConfigItemAttribute)attr)?.DefaultValue ?? ""} |");
            }
            _sharedContent.Save(moduleName, docsDir + "/modules", docBuilder);
        }
    }
}
