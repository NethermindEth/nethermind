// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            string[] configs = { "mainnet.cfg", "goerli.cfg", "sepolia.cfg" };

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

            // Write sample docker-compose .env file
            var env = "mainnet_env";
            _markdownGenerator.CreateTab(docBuilder, env);
            docBuilder.AppendLine("```yaml");
            string[] envData = File.ReadAllLines($"./envs/{env}");

            foreach (string line in envData)
            {
                docBuilder.AppendLine(line);
            }
            docBuilder.AppendLine("```");
            _markdownGenerator.CloseTab(docBuilder);

            _markdownGenerator.CloseTabs(docBuilder);

            string path = string.Concat(docsDir, "/ethereum-client/configuration");
            _sharedContent.Save(moduleName, path, docBuilder);
        }
    }
}
