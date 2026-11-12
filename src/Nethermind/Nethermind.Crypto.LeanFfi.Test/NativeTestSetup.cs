// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[SetUpFixture]
public sealed class NativeTestSetup
{
    [OneTimeSetUp]
    public void RequireNativeTestOptIn()
#if NATIVE_LEAN_TESTS
        => Assert.That(NativeLeanProofVerifier.AbiVersion, Is.EqualTo(12u));
#else
        => Assert.Ignore("The native Lean packages ship Linux libraries only; this suite compiles elsewhere but does not run.");
#endif
}
