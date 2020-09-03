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
        public string Deploy(string address, string contractType)
            => NodeManager.Post<string>("baseline_deploy", CliParseAddress(address), contractType).Result;

        [CliFunction("baseline", "insertLeaf")]
        public string InsertLeaf(string address, string contractAddress, string hash) =>
            NodeManager.Post<string>(
                "baseline_insertLeaf",
                CliParseAddress(address),
                CliParseAddress(contractAddress),
                CliParseHash(hash)).Result;
        
        [CliFunction("baseline", "insertLeaves")]
        public string InsertLeaves(string address, string contractAddress, string[] hashes) => NodeManager.Post<string>(
            "baseline_insertLeaves",
            CliParseAddress(address),
            CliParseAddress(contractAddress),
            hashes.Select(CliParseHash).ToArray()
            ).Result;
        
        [CliFunction("baseline", "getRoot")]
        public string GetRoot(string contactAddress) => NodeManager.Post<string>(
            "baseline_getRoot",
            CliParseAddress(contactAddress)).Result;
        
        [CliFunction("baseline", "getLeaf")]
        public object GetLeaf(string contactAddress, long leafIndex) => NodeManager.PostJint(
            "baseline_getLeaf",
            CliParseAddress(contactAddress),
            leafIndex).Result;
        
        [CliFunction("baseline", "getLeaves")]
        public object GetLeaves(string contactAddress, long[] leafIndexes) => NodeManager.PostJint(
            "baseline_getLeaves",
            CliParseAddress(contactAddress),
            leafIndexes).Result;
        
        [CliFunction("baseline", "getSiblings")]
        public object GetSiblings(string contactAddress, long leafIndex) => NodeManager.PostJint(
            "baseline_getSiblings",
            CliParseAddress(contactAddress),
            leafIndex).Result;
        
        [CliFunction("baseline", "verify")]
        public bool Verify(string contactAddress, string root, string leaf, object path) => NodeManager.Post<bool>(
            "baseline_verify",
            CliParseAddress(contactAddress),
            CliParseHash(root),
            CliParseHash(leaf),
            path).Result;
        
        [CliFunction("baseline", "track")]
        public string Track(string contactAddress) => NodeManager.Post(
            "baseline_track",
            CliParseAddress(contactAddress)).Result;
        
        [CliFunction("baseline", "getTracked")]
        public object GetTracked() => NodeManager.PostJint(
            "baseline_getTracked").Result;
    }
}