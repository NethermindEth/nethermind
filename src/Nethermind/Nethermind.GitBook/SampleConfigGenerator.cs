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
// 

using System.IO;
using System.Text;

namespace Nethermind.GitBook
{
    public class SampleConfigGenerator
    {
        private readonly MarkdownGenerator _markdownGenerator;
        private readonly SharedContent _sharedContent;

        public SampleConfigGenerator(MarkdownGenerator markdownGenerator, SharedContent sharedContent)
        {
            _markdownGenerator = markdownGenerator;
            _sharedContent = sharedContent;
        }

        public void Generate()
        {
            string docsDir = DocsDirFinder.FindDocsDir();
            string runnerDir = DocsDirFinder.FindRunnerDir();
            string moduleName = "sample-configuration";
            string[] configs = {"mainnet.cfg", "goerli.cfg", "rinkeby.cfg", "ropsten.cfg"};

            StringBuilder docBuilder = new StringBuilder();

            docBuilder.AppendLine("---");
            docBuilder.AppendLine("description: Sample Fast Sync configurations for Nethermind");
            docBuilder.AppendLine("---");
            docBuilder.AppendLine();
            docBuilder.AppendLine("# Sample configuration");
            docBuilder.AppendLine();
            _markdownGenerator.OpenTabs(docBuilder);

            foreach (string config in configs)
            {
                _markdownGenerator.CreateTab(docBuilder, config);
                docBuilder.AppendLine("```yaml");
                string[] configData = File.ReadAllLines($"{runnerDir}/configs/{config}");
                
                foreach (string line in configData)
                {
                    docBuilder.AppendLine(line);
                }
                docBuilder.AppendLine("```");
                _markdownGenerator.CloseTab(docBuilder);
            }
            _markdownGenerator.CloseTabs(docBuilder);
            
            string path = string.Concat(docsDir, "/ethereum-client/configuration");
            _sharedContent.Save(moduleName, path, docBuilder);
        }
    }
}
