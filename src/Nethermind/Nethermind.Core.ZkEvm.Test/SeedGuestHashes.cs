// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

/// <summary>Installs the guest hash seed once for the whole assembly.</summary>
/// <remarks>
/// The guest installs it in <c>StatelessExecutor.Execute</c> rather than in a static initializer, which
/// is what keeps a class-initialisation check off every mixer call; a test process reaches the mixers
/// without going through that entry point. Deliberately outside any namespace, so it wraps every fixture
/// here and a new one cannot forget it - hashing unseeded traps rather than throwing.
/// </remarks>
[SetUpFixture]
public class SeedGuestHashes
{
    [OneTimeSetUp]
    public void SeedHashes() => SpanExtensions.SeedHashes(SpanExtensions.DefaultHashSeed);
}
