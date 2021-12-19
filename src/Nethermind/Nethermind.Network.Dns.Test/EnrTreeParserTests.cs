//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
        Assert.AreEqual(enrTreeRootText, actual);
    }
    
    [TestCase("enrtree-branch:TSVUMUTQU3AMKR36PNX4ILDJJI,VPN5OWLF7Q2PBBJUSOYKPQDGFE", 2)]
    [TestCase("enrtree-branch:", 1)]
    public void Can_parse_branch(string enrBranchText, int hashCount)
    {
        EnrTreeBranch branch = EnrTreeParser.ParseBranch(enrBranchText);
        string actual = branch.ToString();
        Assert.AreEqual(hashCount, branch.Hashes.Length);
        Assert.AreEqual(enrBranchText, actual);
    }
    
    [TestCase("enrtree-branch:TSVUMUTQU3AMKR36PNX4ILDJJI,VPN5OWLF7Q2PBBJUSOYKPQDGFE", 2)]
    [TestCase("enrtree-branch:", 1)]
    public void Can_parse_leaf(string enrBranchText, int hashCount)
    {
        EnrTreeBranch branch = EnrTreeParser.ParseBranch(enrBranchText);
        string actual = branch.ToString();
        Assert.AreEqual(hashCount, branch.Hashes.Length);
        Assert.AreEqual(enrBranchText, actual);
    }
    
    [TestCase("enrtree-branch:TSVUMUTQU3AMKR36PNX4ILDJJI,VPN5OWLF7Q2PBBJUSOYKPQDGFE", 2)]
    [TestCase("enrtree-branch:", 1)]
    public void Can_parse_linked_tree(string enrBranchText, int hashCount)
    {
        EnrTreeBranch branch = EnrTreeParser.ParseBranch(enrBranchText);
        string actual = branch.ToString();
        Assert.AreEqual(hashCount, branch.Hashes.Length);
        Assert.AreEqual(enrBranchText, actual);
    }
}
