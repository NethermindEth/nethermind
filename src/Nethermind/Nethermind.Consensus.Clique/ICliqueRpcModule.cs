// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Consensus.Clique
{
    [RpcModule(ModuleType.Clique)]
    public interface ICliqueRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Retrieves the signer of the block with the given hash. Returns error of a block with the given hash does not exist.", IsImplemented = true)]
        ResultWrapper<Address?> clique_getBlockSigner(Keccak? hash);

        [JsonRpcMethod(Description = "Retrieves a snapshot of all clique state at a given block.", IsImplemented = true)]
        ResultWrapper<Snapshot> clique_getSnapshot();

        [JsonRpcMethod(Description = "Retrieves the state snapshot at a given block.", IsImplemented = true)]
        ResultWrapper<Snapshot> clique_getSnapshotAtHash(Keccak hash);

        [JsonRpcMethod(Description = "Retrieves the list of authorized signers.", IsImplemented = true)]
        ResultWrapper<Address[]> clique_getSigners();

        [JsonRpcMethod(Description = "Retrieves the list of authorized signers at the specified block by hash.", IsImplemented = true)]
        ResultWrapper<Address[]> clique_getSignersAtHash(Keccak hash);

        [JsonRpcMethod(Description = "Retrieves the list of authorized signers at the specified block by block number.", IsImplemented = true)]
        ResultWrapper<Address[]> clique_getSignersAtNumber(long number);

        [JsonRpcMethod(Description = "Retrieves the list of authorized signers but with signer names instead of addresses", IsImplemented = true)]
        ResultWrapper<string[]> clique_getSignersAnnotated();

        [JsonRpcMethod(Description = "Retrieves the list of authorized signers at the specified block by hash but with signer names instead of addresses", IsImplemented = true)]
        ResultWrapper<string[]> clique_getSignersAtHashAnnotated(Keccak hash);

        [JsonRpcMethod(Description = "Adds a new authorization proposal that the signer will attempt to push through. If the `vote` parameter is true, the local signer votes for the given address to be included in the set of authorized signers. With `vote` set to false, the signer is against the address.", IsImplemented = true)]
        ResultWrapper<bool> clique_propose(Address signer, bool vote);

        [JsonRpcMethod(Description = "This method drops a currently running proposal. The signer will not cast further votes (either for or against) the address.", IsImplemented = true)]
        ResultWrapper<bool> clique_discard(Address signer);

        [JsonRpcMethod(Description = "Forces Clique block producer to produce a new block", IsImplemented = true)]
        ResultWrapper<bool> clique_produceBlock(Keccak parentHash);
    }
}
