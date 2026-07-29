// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class BootnodeOptionValidationTests
{
    [TestCase(1)]
    [TestCase(65535)]
    public void Port_range_accepts_valid_ports(int port) =>
        Assert.DoesNotThrow(() => BootnodeOptionValidation.ValidatePort("--port", port));

    [TestCase(0)]
    [TestCase(65536)]
    public void Port_range_rejects_invalid_ports(int port) =>
        Assert.That(
            () => BootnodeOptionValidation.ValidatePort("--port", port),
            Throws.TypeOf<ArgumentOutOfRangeException>());

    [TestCase("--bucket-size")]
    [TestCase("--concurrency")]
    [TestCase("--discovery-interval-ms")]
    public void Positive_options_reject_zero(string optionName) =>
        Assert.That(
            () => BootnodeOptionValidation.ValidatePositive(optionName, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void Active_discovery_jobs_rejects_negative_values() =>
        Assert.That(
            () => BootnodeOptionValidation.ValidateNonNegative("--active-discovery-jobs", -1),
            Throws.TypeOf<ArgumentOutOfRangeException>());
}
