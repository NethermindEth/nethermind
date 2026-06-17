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

    [Test]
    public void Can_parse_leaf()
    {
        const string enr = "enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8";
        EnrTreeNode node = EnrTreeParser.ParseNode(enr);
        Assert.That(node, Is.TypeOf<EnrLeaf>());
        EnrLeaf leaf = (EnrLeaf)node;
        Assert.That(leaf.ToString(), Is.EqualTo(enr));
        Assert.That(leaf.Links, Is.Empty);
        Assert.That(leaf.Refs, Is.Empty);
        Assert.That(leaf.Records.Length, Is.EqualTo(1));
        Assert.That(leaf.Records[0], Is.EqualTo(enr));
        Assert.That(leaf.NodeRecord, Is.EqualTo(enr.Substring(4)));
    }

    [TestCase("enrtree://all.mainnet.ethdisco.net")]
    [TestCase("enrtree://AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE@all.mainnet.ethdisco.net")]
    public void Can_parse_linked_tree(string text)
    {
        EnrTreeNode node = EnrTreeParser.ParseNode(text);
        Assert.That(node, Is.TypeOf<EnrLinkedTree>());
        EnrLinkedTree linked = (EnrLinkedTree)node;
        Assert.That(linked.ToString(), Is.EqualTo(text));
        Assert.That(linked.Links.Length, Is.EqualTo(1));
        Assert.That(linked.Links[0], Is.EqualTo(text.Substring("enrtree://".Length)));
        Assert.That(linked.Refs, Is.Empty);
        Assert.That(linked.Records, Is.Empty);
    }
}
