// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.GitBook
{
    class Program
    {
        static void Main(string[] args)
        {
            MarkdownGenerator markdownGenerator = new MarkdownGenerator();
            SharedContent sharedContent = new SharedContent();

            MetricsGenerator metricsGenerator = new MetricsGenerator(sharedContent);
            metricsGenerator.Generate();

            ConfigGenerator configGenerator = new ConfigGenerator(sharedContent);
            configGenerator.Generate();

            RpcAndCliGenerator rpcAndCliGenerator = new RpcAndCliGenerator(markdownGenerator, sharedContent);
            rpcAndCliGenerator.Generate();

            SampleConfigGenerator sampleConfigGenerator = new SampleConfigGenerator(markdownGenerator, sharedContent);
            sampleConfigGenerator.Generate();
        }
    }
}
