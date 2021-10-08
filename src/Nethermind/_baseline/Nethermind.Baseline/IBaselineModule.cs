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

using System.Threading.Tasks;
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Baseline
{
    [RpcModule(ModuleType.Baseline)]
    public interface IBaselineModule : IRpcModule
    {
        [JsonRpcMethod(
            Description = "(DEV only - not part of Baseline standard) Inserts a single leaf to a tree at the given 'address'",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<Keccak>> baseline_insertCommit(Address address, Address contractAddress, Keccak hash);

        [JsonRpcMethod(
            Description = "(DEV only - not part of Baseline standard) Inserts multiple leaves to a tree at the given 'address'",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<Keccak>> baseline_insertCommits(
            Address address,
            Address contractAddress,
            params Keccak[] hash);

        [JsonRpcMethod(
            Description = "Gets a single leaf from a tree at the given 'address'",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<BaselineTreeNode>> baseline_getCommit(
            Address contractAddress,
            UInt256 leafIndex,
            BlockParameter? blockParameter = null);
        
        [JsonRpcMethod(
            Description = "Gets root of a tree at the given 'address'",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<Keccak>> baseline_getRoot(
            Address contractAddress,
            BlockParameter? blockParameter = null);

        [JsonRpcMethod(
            Description = "(DEV only - not part of Baseline standard) Gets count of a tree at the given 'address'",
            IsSharable = true,
            IsImplemented = true)]
        public Task<ResultWrapper<long>> baseline_getCount(
            Address contractAddress,
            BlockParameter? blockParameter = null);

        [JsonRpcMethod(
            Description = "Gets multiple leaves from a tree at the given 'address'",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<BaselineTreeNode[]>> baseline_getCommits(
            Address contractAddress,
            UInt256[] leafIndexes,
            BlockParameter? blockParameter = null);

        [JsonRpcMethod(
            Description = "(DEV only - not part of Baseline standard) Deploys a contract with the given 'contract type'. Requires the account to be unlocked.",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<Keccak>> baseline_deploy(Address address, string contractType, string? argumentsAbi = null);
        
        [JsonRpcMethod(
            Description = "(DEV only - not part of Baseline standard) Deploys a contract with the given bytecode. Requires the account to be unlocked.",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<Keccak>> baseline_deployBytecode(Address address, string byteCode);

        [JsonRpcMethod(
            Description = "Gets siblings path / proof of the given leaf.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<BaselineTreeNode[]>> baseline_getSiblings(
            Address contractAddress,
            long leafIndex,
            BlockParameter? blockParameter = null);
        
        [JsonRpcMethod(
            Description = "Verifies a sibling path for a given root and leaf value.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> baseline_verify(
            Address contractAddress,
            Keccak root,
            Keccak leaf,
            BaselineTreeNode[] siblingsPath,
            BlockParameter? blockParameter = null);

        [JsonRpcMethod(
            Description = "Starts tracking a tree at the given address.",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> baseline_track(Address contractAddress);
        
        [JsonRpcMethod(
            Description = "Stops tracking a tree at the given address.",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> baseline_untrack(Address contractAddress);

        [JsonRpcMethod(
            Description = "Lists all the tracked tree addresses.",
            IsSharable = false,
            IsImplemented = false)]
        Task<ResultWrapper<Address[]>> baseline_getTracked();

        [JsonRpcMethod(
            Description = "Verify data and push new input.",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<VerifyAndPushResponse>> baseline_verifyAndPush(
            Address address,
            Address contractAddress,
            UInt256[] proof,
            UInt256[] publicInputs,
            Keccak newCommitment);
    }
    
    public class VerifyAndPushResponse
    {
        public VerifyAndPushResponse(Keccak txHash)
        {
            TxHash = txHash;
        }
        
        public Commitment? Commitment { get; set; }
        public Keccak? TxHash { get; set; }
    }
    
    public class Commitment
    {
        public Commitment(long location, Keccak value)
        {
            Location = location;
            Value = value;
        }
        
        public long Location { get; set; }
        public Keccak Value { get; set; }
    }
}
