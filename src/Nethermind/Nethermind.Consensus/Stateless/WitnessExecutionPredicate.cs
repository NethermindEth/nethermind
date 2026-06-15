// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Per-scope signal that block execution in this scope serves witness purposes — recording on the
/// main pipeline (<see cref="IsActive"/> tracks the armed <see cref="WitnessCaptureSession"/>),
/// recording in the legacy <c>debug_executionWitness</c> sandbox, or stateless verification.
/// </summary>
/// <remarks>
/// Drives <c>BlockAccessListManager</c> to force sequential execution and bypass the shared code
/// cache while <see cref="IsActive"/> returns <c>true</c>. Registered only in witness-capable scopes;
/// its absence (the common case) leaves BAL on the fast parallel + cached path. Evaluated per block,
/// so on the main pipeline it is active only for the specific block being witnessed.
/// </remarks>
public sealed record WitnessExecutionPredicate(Func<bool> IsActive);
