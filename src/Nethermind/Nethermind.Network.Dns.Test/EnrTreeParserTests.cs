// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

[TestFixture]
public class EnrTreeParserTests
{
    [TestCase("enrtree-root:v1 e=TPLRUM3FAKJZIRMXADWOHSU3PM l=FDXN3SN67NA5DKA4J2GOK7BVQI seq=2779 sig=CNoJofW_lNh7QFQkaVGhEX2ifbEZ3UkiBQCVyZCkM_I-72cEh8Bfd21cSS9BP5tyAqWF3jMVov8duUCdSByEQAE")]
    public void Can_parse_sample_root_texts(string enrTreeRootText)
    {
        EnrTreeRoot root = EnrTreeParser.ParseEnrRoot(enrTreeRootText);
        string actual = root.ToString();
        Assert.That(actual, Is.EqualTo(enrTreeRootText));
    }

    [TestCase("enrtree-branch:TSVUMUTQU3AMKR36PNX4ILDJJI,VPN5OWLF7Q2PBBJUSOYKPQDGFE", 2)]
    [TestCase("enrtree-branch:", 1)]
    public void Can_parse_branch(string enrBranchText, int hashCount)
    {
        EnrTreeBranch branch = EnrTreeParser.ParseBranch(enrBranchText);
        string actual = branch.ToString();
        Assert.That(branch.Hashes.Length, Is.EqualTo(hashCount));
        Assert.That(actual, Is.EqualTo(enrBranchText));
    }

    [TestCase("enrtree-branch:TSVUMUTQU3AMKR36PNX4ILDJJI,VPN5OWLF7Q2PBBJUSOYKPQDGFE", 2)]
    [TestCase("enrtree-branch:", 1)]
    public void Can_parse_leaf(string enrBranchText, int hashCount)
    {
        EnrTreeBranch branch = EnrTreeParser.ParseBranch(enrBranchText);
        string actual = branch.ToString();
        Assert.That(branch.Hashes.Length, Is.EqualTo(hashCount));
        Assert.That(actual, Is.EqualTo(enrBranchText));
    }

    [TestCase("enrtree-branch:TSVUMUTQU3AMKR36PNX4ILDJJI,VPN5OWLF7Q2PBBJUSOYKPQDGFE", 2)]
    [TestCase("enrtree-branch:", 1)]
    public void Can_parse_linked_tree(string enrBranchText, int hashCount)
    {
        EnrTreeBranch branch = EnrTreeParser.ParseBranch(enrBranchText);
        string actual = branch.ToString();
        Assert.That(branch.Hashes.Length, Is.EqualTo(hashCount));
        Assert.That(actual, Is.EqualTo(enrBranchText));
    }
}
