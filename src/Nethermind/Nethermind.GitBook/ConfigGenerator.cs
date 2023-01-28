// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
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

            string[] dlls = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll")
                .OrderBy(n => n).ToArray();

            foreach (string dll in dlls)
            {
                Assembly assembly = Assembly.LoadFile(dll);
                Type[] modules = assembly.GetExportedTypes().Where(t => typeof(IConfig).IsAssignableFrom(t) && t.IsInterface).ToArray();

                foreach (Type module in modules)
                {
                    GenerateDocFileContent(module, docsDir);
                }
            }
        }

        private void GenerateDocFileContent(Type configType, string docsDir)
        {
            Attribute attribute = configType.GetCustomAttribute(typeof(ConfigCategoryAttribute));
            if (((ConfigCategoryAttribute)attribute)?.HiddenFromDocs ?? false) return;

            StringBuilder docBuilder = new StringBuilder();
            string moduleName = configType.Name.Substring(1).Replace("Config", "");

            PropertyInfo[] moduleProperties = configType.GetProperties().OrderBy(p => p.Name).ToArray();

            docBuilder.AppendLine(@$"# {moduleName}");
            moduleName = moduleName.ToLower();
            docBuilder.AppendLine();
            docBuilder.AppendLine($"{((ConfigCategoryAttribute)attribute)?.Description ?? ""}");
            docBuilder.AppendLine();
            docBuilder.AppendLine("| Property | Env Variable | Description | Default |");
            docBuilder.AppendLine("| :--- | :--- | :--- | :--- |");

            if (moduleProperties.Length == 0) return;

            foreach (PropertyInfo property in moduleProperties)
            {
                Attribute attr = property.GetCustomAttribute(typeof(ConfigItemAttribute));
                if (((ConfigItemAttribute)attr)?.HiddenFromDocs ?? false) continue;
                docBuilder.AppendLine($"| {property.Name} | NETHERMIND_{moduleName.ToUpper()}CONFIG_{property.Name.ToUpper()} | {((ConfigItemAttribute)attr)?.Description ?? ""} | {((ConfigItemAttribute)attr)?.DefaultValue ?? ""} |");
            }

            string path = string.Concat(docsDir, "/ethereum-client/configuration");
            _sharedContent.Save(moduleName, path, docBuilder);
        }
    }
}
