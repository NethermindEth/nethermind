// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Clique
{
    public interface ISnapshotManager
    {
        ulong GetLastSignersCount();
        Snapshot GetOrCreateSnapshot(long number, Keccak hash);
        Address GetBlockSealer(BlockHeader header);
        bool IsValidVote(Snapshot snapshot, Address address, bool authorize);
        bool IsInTurn(Snapshot snapshot, long number, Address signer);
        bool HasSignedRecently(Snapshot snapshot, long number, Address signer);
    }
}
