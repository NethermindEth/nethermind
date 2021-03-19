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
            
            //ToFix: Create MetricsCategoryAttribute and add MetricsCategory attribute to every "Metrics" class and load Metrics automatically instead of manually
            Type metricsType = Assembly.Load("Nethermind.Blockchain").GetTypes()
                                        .First(t => typeof(Blockchain.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "Blockchain", docsDir);
            
            metricsType = Assembly.Load("Nethermind.Evm").GetTypes()
                .First(t => typeof(Evm.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "Evm", docsDir);
            
            metricsType = Assembly.Load("Nethermind.JsonRpc").GetTypes()
                .First(t => typeof(JsonRpc.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "JsonRpc", docsDir);
            
            metricsType = Assembly.Load("Nethermind.Network").GetTypes()
                .First(t => typeof(Network.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "Network", docsDir);
            
            metricsType = Assembly.Load("Nethermind.Db").GetTypes()
                .First(t => typeof(Db.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "Store", docsDir);
            
            metricsType = Assembly.Load("Nethermind.Consensus.AuRa").GetTypes()
                .First(t => typeof(Consensus.AuRa.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "Consensus.Aura", docsDir);
            
            metricsType = Assembly.Load("Nethermind.Runner").GetTypes()
                .First(t => typeof(Runner.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "Runner", docsDir);
            
            metricsType = Assembly.Load("Nethermind.Synchronization").GetTypes()
                .First(t => typeof(Synchronization.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "Synchronization", docsDir);
            
            metricsType = Assembly.Load("Nethermind.Trie").GetTypes()
                .First(t => typeof(Trie.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "Trie", docsDir);
            
            metricsType = Assembly.Load("Nethermind.TxPool").GetTypes()
                .First(t => typeof(TxPool.Metrics).IsAssignableFrom(t));
            GenerateDocFileContent(metricsType, "TxPool", docsDir);
        }

        private void GenerateDocFileContent(Type metricsType, string moduleName, string docsDir)
        {
            StringBuilder docBuilder = new StringBuilder();

            PropertyInfo[] moduleProperties = metricsType.GetProperties().OrderBy(x => x.Name).ToArray();

            docBuilder.AppendLine(@$"# {moduleName}");
            docBuilder.AppendLine();
            moduleName = moduleName.ToLower();
            docBuilder.AppendLine("| Metric | Description |");
            docBuilder.AppendLine("| :--- | :--- |");
            
            if (moduleProperties.Length == 0) return;
            
            foreach (PropertyInfo property in moduleProperties)
            {
                Attribute attr = property.GetCustomAttribute(typeof(DescriptionAttribute));
                docBuilder.AppendLine($"| {property.Name} | {((DescriptionAttribute)attr)?.Description ?? ""} |");
            }
            _sharedContent.Save(moduleName, docsDir + "/ethereum-client/metrics", docBuilder);
        }
    }
}
