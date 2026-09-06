// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

public class GuestHashSeedTests
{
    /// <remarks>
    /// The saving this guards is a property of the whole type, not of one file: a single static field
    /// initializer in any partial compiled into the guest re-emits the class constructor and puts a
    /// class-initialisation check, a fence and a two-level static load back on every mixer call - about
    /// 134,000 of them per block. Nothing else in CI would notice, since the guest job compares output
    /// bytes rather than step counts.
    /// </remarks>
    [Test]
    public void Guest_hash_type_has_no_class_constructor() =>
        Assert.That(typeof(SpanExtensions).TypeInitializer, Is.Null);
}
