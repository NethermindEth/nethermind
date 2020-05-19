namespace Nethermind.Cli.Modules
{
    [CliModule("baseline")]
    public class BaselineCliModule : CliModuleBase
    {
        public BaselineCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }

        [CliFunction("baseline", "deploy")]
        public string deploy(string address, string contractType) => NodeManager.Post<string>("baseline_deploy", CliParseAddress(address), contractType).Result;

        [CliFunction("baseline", "addLeaf")]
        public string addLeaf() => NodeManager.Post<string>("baseline_addLeaf").Result;
        
        [CliFunction("baseline", "addLeaves")]
        public string addLeaves() => NodeManager.Post<string>("baseline_addLeaves").Result;
        
        [CliFunction("baseline", "getSiblings")]
        public string getSiblings() => NodeManager.Post("baseline_getSiblings").Result;
    }
}