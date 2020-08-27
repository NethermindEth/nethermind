namespace Nethermind.GitBook
{
    class Program
    {
        static void Main(string[] args)
        {
            JsonRpcGenerator rpcGenerator = new JsonRpcGenerator();
            rpcGenerator.Generate();
        }
    }
}
