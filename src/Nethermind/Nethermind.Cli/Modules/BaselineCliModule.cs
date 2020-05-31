using System.Linq;

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
        public string addLeaf(string address, string contractAddress, string hash) =>
            NodeManager.Post<string>(
                "baseline_addLeaf",
                CliParseAddress(address),
                CliParseAddress(contractAddress),
                CliParseHash(hash)).Result;
        
        [CliFunction("baseline", "addLeaves")]
        public string addLeaves(string address, string contractAddress, string[] hashes) => NodeManager.Post<string>(
            "baseline_addLeaves",
            CliParseAddress(address),
            CliParseAddress(contractAddress),
            hashes.Select(CliParseHash).ToArray()
            ).Result;
        
        [CliFunction("baseline", "getSiblings")]
        public string getSiblings(string contactAddress, long leafIndex) => NodeManager.Post(
            "baseline_getSiblings",
            CliParseAddress(contactAddress),
            leafIndex).Result;
        
        [CliFunction("baseline", "track")]
        public string getSiblings(string contactAddress) => NodeManager.Post(
            "baseline_track",
            CliParseAddress(contactAddress)).Result;
    }
}