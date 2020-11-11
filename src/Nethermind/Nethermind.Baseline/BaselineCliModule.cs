//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Linq;
using Nethermind.Cli;
using Nethermind.Cli.Modules;

namespace Nethermind.Baseline
{
    [CliModule("baseline")]
    public class BaselineCliModule : CliModuleBase
    {
        public BaselineCliModule(ICliEngine cliEngine, INodeManager nodeManager)
            : base(cliEngine, nodeManager)
        {
        }

        [CliFunction("baseline", "deploy")]
        public string Deploy(string address, string contractType, string? argumentsAbi)
        {
            if (argumentsAbi == null)
            {
                return NodeManager.Post<string>("baseline_deploy", CliParseAddress(address), contractType).Result;    
            }

            return NodeManager.Post<string>("baseline_deploy", CliParseAddress(address), contractType, argumentsAbi).Result;
        }
            

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
        public string GetRoot(string contractAddress, string? blockParameter) => NodeManager.Post<string>(
            "baseline_getRoot",
            CliParseAddress(contractAddress),
             blockParameter ?? "latest").Result;
        
        [CliFunction("baseline", "getCount")]
        public string GetCount(string contractAddress, string? blockParameter) => NodeManager.Post<string>(
            "baseline_getCount",
            CliParseAddress(contractAddress),
            blockParameter ?? "latest").Result;
        
        [CliFunction("baseline", "getLeaf")]
        public object GetLeaf(string contractAddress, long leafIndex, string? blockParameter) => NodeManager.PostJint(
            "baseline_getLeaf",
            CliParseAddress(contractAddress),
            leafIndex,
            blockParameter ?? "latest").Result;
        
        [CliFunction("baseline", "getLeaves")]
        public object GetLeaves(string contractAddress, long[] leafIndexes, string? blockParameter) => NodeManager.PostJint(
            "baseline_getLeaves",
            CliParseAddress(contractAddress),
            leafIndexes,
            blockParameter ?? "latest").Result;
        
        [CliFunction("baseline", "getSiblings")]
        public object GetSiblings(string contractAddress, long leafIndex, string? blockParameter) => NodeManager.PostJint(
            "baseline_getSiblings",
            CliParseAddress(contractAddress),
            leafIndex,
            blockParameter ?? "latest").Result;
        
        [CliFunction("baseline", "verify")]
        public bool Verify(string contractAddress, string root, string leaf, object path, string? blockParameter) => NodeManager.Post<bool>(
            "baseline_verify",
            CliParseAddress(contractAddress),
            CliParseHash(root),
            CliParseHash(leaf),
            path,
            blockParameter ?? "latest").Result;
        
        [CliFunction("baseline", "verifyAndPush")]
        public bool VerifyAndPush(string address, string contractAddress, object proof, object publicInputs, string commitment) => NodeManager.Post<bool>(
            "baseline_verifyAndPush",
            CliParseAddress(address),
            CliParseAddress(contractAddress),
            proof,
            publicInputs,
            CliParseHash(commitment)).Result;
        
        [CliFunction("baseline", "track")]
        public string Track(string contractAddress) => NodeManager.Post(
            "baseline_track",
            CliParseAddress(contractAddress)).Result;
        
        [CliFunction("baseline", "getTracked")]
        public object GetTracked() => NodeManager.PostJint(
            "baseline_getTracked").Result;
    }
}
