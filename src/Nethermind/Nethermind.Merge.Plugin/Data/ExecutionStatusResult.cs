// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Data
{
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
    public class ExecutionStatusResult
    {
        public ExecutionStatusResult(Keccak headBlockHash, Keccak finalizedBlockHash, Keccak safeBlockHash)
        {
            HeadBlockHash = headBlockHash;
            FinalizedBlockHash = finalizedBlockHash;
            SafeBlockHash = safeBlockHash;
        }
        public Keccak HeadBlockHash { get; set; }
        public Keccak FinalizedBlockHash { get; set; }
        public Keccak SafeBlockHash { get; set; }
    }
}
