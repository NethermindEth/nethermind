// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// A rollup settled by a batch: the proof systems it requires (indexes into the batch's strictly increasing list,
/// themselves strictly increasing), the verification key it registered for each, and its manager's custom data.
/// </summary>
public sealed record RollupProofAssignment(ulong RollupId, ulong[] ProofSystemIndexes, ValueHash256[] VerificationKeys, byte[] CustomData);
