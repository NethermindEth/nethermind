// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

/// <param name="GetBlock">
/// Reconstructs the block. Deferred because it parses attacker-controlled transaction RLP and can
/// throw, so callers must publish <see cref="StatelessExecutor.FailureOutput"/> before invoking it.
/// </param>
internal readonly record struct StatelessPayload
(
    Func<Block> GetBlock,
    ExecutionWitness Witness,
    ulong ChainId,
    ushort SchemaId,
    ReadOnlyMemory<SszPublicKey> PublicKeys,
    ReadOnlyMemory<Hash256> VersionedHashes,
    Hash256 NewPayloadRequestRoot,
    ISpecProvider SpecProvider
);
