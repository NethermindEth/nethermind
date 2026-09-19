// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Ssz.Test;

/// <summary>
/// Covers <see cref="TestFixtureDownloader.PathUnderPrefix"/> in isolation: it is a pure string
/// predicate, so these run with no download and no filesystem access.
/// </summary>
[TestFixture]
public class TestFixtureDownloaderTests
{
    [TestCase("tests/general/phase0/ssz_generic", "tests/general/phase0/ssz_generic", true, Description = "exact match")]
    [TestCase("tests/general/phase0/ssz_generic/uints/valid/uint_8.data", "tests/general/phase0/ssz_generic", true, Description = "descendant file")]
    [TestCase("tests/general/phase0/ssz_generic/", "tests/general/phase0/ssz_generic", true, Description = "descendant with trailing slash matching the directory entry itself")]
    [TestCase("tests/general/phase0/ssz_generic_extra/uints", "tests/general/phase0/ssz_generic", false, Description = "sibling that merely shares the prefix string must not match")]
    [TestCase("tests/general/phase0/ssz_static/uints", "tests/general/phase0/ssz_generic", false, Description = "unrelated sibling directory")]
    [TestCase("tests/general/phase0", "tests/general/phase0/ssz_generic", false, Description = "ancestor of the prefix is not under it")]
    [TestCase("./tests/general/phase0/ssz_generic/uints", "tests/general/phase0/ssz_generic", true, Description = "leading './' from GNU tar is stripped")]
    [TestCase("tests\\general\\phase0\\ssz_generic\\uints", "tests/general/phase0/ssz_generic", true, Description = "backslash separators are normalized")]
    [TestCase("tests/general/phase0/ssz_generic/uints", "./tests/general/phase0/ssz_generic/", true, Description = "the prefix itself is normalized too")]
    [TestCase("tests/general/altair/bls/eth_aggregate_pubkeys", "tests/general/phase0/ssz_generic", false, Description = "the unrelated content real archives carry alongside ssz_generic")]
    public void PathUnderPrefix_matches_on_directory_boundary(string entryPath, string prefix, bool expected) =>
        Assert.That(TestFixtureDownloader.PathUnderPrefix(entryPath, prefix), Is.EqualTo(expected));
}
