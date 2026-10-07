// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Attester;

/// <param name="ChainConfigPath">The L2 genesis, or its bare chain configuration.</param>
/// <param name="VerificationKey">The proof system's verification key for this attester.</param>
/// <param name="BlockTimeSeconds">The L2 cadence derivation adds to each parent's timestamp.</param>
public sealed record AttesterOptions(
    IPEndPoint ListenAddress,
    string ChainConfigPath,
    ulong RollupId,
    ValueHash256 VerificationKey,
    Address AttesterAddress,
    Address ProofSystem,
    string KeyStoreDirectory,
    string PasswordFile,
    ulong BlockTimeSeconds,
    ulong GasLimit,
    WindowLimits Limits,
    TimeSpan StreamIdleTimeout,
    TimeSpan RequestTimeout);
