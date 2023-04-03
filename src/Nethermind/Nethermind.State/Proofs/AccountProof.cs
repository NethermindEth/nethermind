// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style account proof
    /// </summary>
    public class AccountProof
    {
        public Address? Address { get; set; }

        public byte[][]? Proof { get; set; }

        public UInt256 Balance { get; set; }

        public Keccak CodeHash { get; set; } = Keccak.OfAnEmptyString;

        public UInt256 Nonce { get; set; }

        public Keccak StorageRoot { get; set; } = Keccak.EmptyTreeHash;

        public StorageProof[]? StorageProofs { get; set; }
    }
}
