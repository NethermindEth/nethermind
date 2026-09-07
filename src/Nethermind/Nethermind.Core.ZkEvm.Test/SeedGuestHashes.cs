// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

/// <summary>Installs the guest hash seed once for the whole assembly.</summary>
/// <remarks>
/// The guest installs it while decoding the payload it is about to execute, rather than in a static
/// initializer, which is what keeps a class-initialisation check off every mixer call; a test process
/// reaches the mixers without decoding a payload, and has no payload root to seed from. Deliberately
/// outside any namespace, so it wraps every fixture here and a new one cannot forget it. Unseeded
/// release hashing is not guaranteed to fail: the scalar word and variable-width paths can use a zero seed.
/// </remarks>
[SetUpFixture]
public class SeedGuestHashes
{
    /// <summary>The seed a test process installs, standing in for a payload root.</summary>
    public static readonly UInt256 Seed = new(0xAC320C7E23EBA0EFUL, 0x2E2473DDDBD55172UL,
        0x0C564BCB0D425343UL, 0x21FAE39C24D6EB90UL);

    [OneTimeSetUp]
    public void SeedHashes() => SpanExtensions.SeedHashes(Seed);
}
