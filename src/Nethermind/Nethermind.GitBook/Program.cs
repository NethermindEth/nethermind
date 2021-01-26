namespace Nethermind.GitBook
{
    class Program
    {
        static void Main(string[] args)
        {
            MarkdownGenerator markdownGenerator = new MarkdownGenerator();
            SharedContent sharedContent = new SharedContent();

            JsonRpcGenerator rpcGenerator = new JsonRpcGenerator(markdownGenerator, sharedContent);
            rpcGenerator.Generate();
            
            CliGenerator cliGenerator = new CliGenerator(markdownGenerator, sharedContent);
            cliGenerator.Generate();

            MetricsGenerator metricsGenerator = new MetricsGenerator(sharedContent);
            metricsGenerator.Generate();

            ConfigGenerator configGenerator = new ConfigGenerator(sharedContent);
            configGenerator.Generate();
        }
    }
}
