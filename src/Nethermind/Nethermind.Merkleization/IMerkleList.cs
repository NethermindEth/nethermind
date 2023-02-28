// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merkleization;

public interface IMerkleList
{
    Root Root { get; }

    uint Count { get; }

    void Insert(Bytes32 leaf);

    IList<Bytes32> GetProof(in uint index);

    bool VerifyProof(Bytes32 leaf, IReadOnlyList<Bytes32> proof, in uint leafIndex);
}
