// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>The deployment an attester signs for: the rollup, its L2 chain and the proof system that verifies it.</summary>
public sealed record EezSettlementContext(ulong RollupId, ulong ChainId, Address ProofSystem, ValueHash256 VerificationKey);
