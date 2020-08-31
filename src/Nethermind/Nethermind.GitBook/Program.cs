namespace Nethermind.GitBook
{
    class Program
    {
        static void Main(string[] args)
        {
            MarkdownGenerator markdownGenerator = new MarkdownGenerator();

            JsonRpcGenerator rpcGenerator = new JsonRpcGenerator(markdownGenerator);
            rpcGenerator.Generate();
        }
    }
}
