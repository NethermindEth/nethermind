// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

internal readonly record struct StatelessPayload
(
    Block Block,
    ExecutionWitness Witness,
    ChainConfig ChainConfig,
    ReadOnlyMemory<SszPublicKeys> PublicKeys,
    Hash256 NewPayloadRequestRoot,
    ProtocolFork ProtocolFork
);
