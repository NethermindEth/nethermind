// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

/// <summary>Installs the guest hash seed once for the whole assembly.</summary>
/// <remarks>
/// The guest installs it while decoding the payload it is about to execute, rather than in a static
/// initializer, which is what keeps a class-initialisation check off every mixer call; a test process
/// reaches the mixers without decoding a payload, and has no payload root to seed from. Deliberately
/// outside any namespace, so it wraps every fixture here and a new one cannot forget it - hashing
/// unseeded traps rather than throwing.
/// </remarks>
[SetUpFixture]
public class SeedGuestHashes
{
    /// <summary>The seed a test process installs, standing in for a payload root.</summary>
    public const uint Seed = 2098026241U;

    [OneTimeSetUp]
    public void SeedHashes() => SpanExtensions.SeedHashes(Seed);
}
