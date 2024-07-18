// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style storage proof
    /// </summary>
    public class StorageProof
    {
        public byte[]? Key { get; set; }
        public byte[][]? Proof { get; set; }
        public ReadOnlyMemory<byte>? Value { get; set; }
    }
}
