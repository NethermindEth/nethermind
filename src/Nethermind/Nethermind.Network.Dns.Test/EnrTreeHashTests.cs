// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class EnrTreeHashTests
{
    private const string Branch = "enrtree-branch:LK4XPBOGFJGFAZQU5EDM5QRCPI,ISE6C2QIK4C66JK4Z47XCKUZCI";

    [TestCase("SNIGOIP7I67HGIGFKVFYHSSDYM", Branch, TestName = "label produced by the reference implementation")]
    [TestCase("SNIGOIP7I67HGIGFKVFYHSSD", Branch, TestName = "truncated label still binds its own prefix (15 bytes)")]
    [TestCase("SNIGOIP7I67HGIGFKVFY", Branch, TestName = "shortest abbreviation the reference implementation accepts (12 bytes)")]
    public void Matching_entry_is_accepted(string subdomain, string entryText)
        => Assert.That(EnrTreeHash.Matches(subdomain, entryText), Is.True);

    [TestCase("WPQJRTERLC2GC666M3Z3YKV3Z4", Branch, TestName = "label of different content")]
    [TestCase("SNIGOIP7I67HGIGFKVFYHSSDYM", "enrtree-branch:LK4XPBOGFJGFAZQU5EDM5QRCPI,AAE6C2QIK4C66JK4Z47XCKUZCI", TestName = "one tampered character in content")]
    [TestCase("SNIGOIP7I67HGIGFKVFYHSSDY1", Branch, TestName = "digit outside the base32 alphabet")]
    [TestCase("snigoip7i67hgigfkvfyhssdym", Branch, TestName = "lower case label")]
    [TestCase("SNIGOIP7I67HGIGFKVF", Branch, TestName = "abbreviation one byte below the floor (11 bytes)")]
    [TestCase("SNIGOIP7I67HGIGF", Branch, TestName = "abbreviation well below the floor (10 bytes)")]
    [TestCase("", Branch, TestName = "empty label must not match everything")]
    [TestCase("SNIGOIP7I67HGIGFKVFYHSSDYMSNIGOIP7I67HGIGFKVFYHSSDYMSNIGOIP7", Branch, TestName = "label longer than a keccak256 hash")]
    public void Mismatching_entry_is_rejected(string subdomain, string entryText)
        => Assert.That(EnrTreeHash.Matches(subdomain, entryText), Is.False);
}
