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

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
public class VerkleTreeTests
{
    private readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
    private readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;
    private readonly Account _account2 = Build.An.Account.WithBalance(2).TestObject;
    private readonly Account _account3 = Build.An.Account.WithBalance(3).TestObject;
    private readonly byte[] treeKeyVersion =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
        224, 183, 72, 25, 6, 8, 210, 159, 31, 0
    };

    private readonly byte[] treeKeyBalance =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
        224, 183, 72, 25, 6, 8, 210, 159, 31, 1
    };
        
    private readonly byte[] treeKeyNonce =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
        224, 183, 72, 25, 6, 8, 210, 159, 31, 2
    };
        
    private readonly byte[] treeKeyCodeKeccak =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
        224, 183, 72, 25, 6, 8, 210, 159, 31, 3
    };

    private readonly byte[] treeKeyCodeSize =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
        224, 183, 72, 25, 6, 8, 210, 159, 31, 4
    };
    
    [SetUp]
    public void Setup()
    {
        Trie.Metrics.TreeNodeHashCalculations = 0;
        Trie.Metrics.TreeNodeRlpDecodings = 0;
        Trie.Metrics.TreeNodeRlpEncodings = 0;
    }
    
    // [Test]
    // public void Get_Account_Keys()
    // {
    //     VerkleStateTree tree = new(LimboLogs.Instance);
    //     byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
    //     Assert.AreEqual(treeKeys.Length, 5);
    //     Assert.AreEqual(treeKeys[AccountTreeIndexes.Version], treeKeyVersion);
    //     Assert.AreEqual(treeKeys[AccountTreeIndexes.Balance], treeKeyBalance);
    //     Assert.AreEqual(treeKeys[AccountTreeIndexes.Nonce], treeKeyNonce);
    //     Assert.AreEqual(treeKeys[AccountTreeIndexes.CodeHash], treeKeyCodeKeccak);
    //     Assert.AreEqual(treeKeys[AccountTreeIndexes.CodeSize], treeKeyCodeSize);
    // }
    
    [Test]
    public void Get_Account_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] keyPrefix = tree.GetTreeKeyPrefixAccount(TestItem.AddressA);
        keyPrefix[31] = AccountTreeIndexes.Version;
        Assert.AreEqual(keyPrefix, treeKeyVersion);
        keyPrefix[31] = AccountTreeIndexes.Balance;
        Assert.AreEqual(keyPrefix, treeKeyBalance);
        keyPrefix[31] = AccountTreeIndexes.Nonce;
        Assert.AreEqual(keyPrefix, treeKeyNonce);
        keyPrefix[31] = AccountTreeIndexes.CodeHash;
        Assert.AreEqual(keyPrefix, treeKeyCodeKeccak);
        keyPrefix[31] = AccountTreeIndexes.CodeSize;
        Assert.AreEqual(keyPrefix, treeKeyCodeSize);
    }
    
    [Test]
    public void Set_Get_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] keyPrefix = tree.GetTreeKeyPrefixAccount(TestItem.AddressA);
        
        byte[] value =  {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
        };
        
        tree.SetValue(keyPrefix,AccountTreeIndexes.Version, value);
        tree.SetValue(keyPrefix,AccountTreeIndexes.Balance, value);
        tree.SetValue(keyPrefix,AccountTreeIndexes.Nonce, value);
        tree.SetValue(keyPrefix,AccountTreeIndexes.CodeHash, value);
        tree.SetValue(keyPrefix,AccountTreeIndexes.CodeSize, value);

        tree.GetValue(keyPrefix, AccountTreeIndexes.Version).Should().BeEquivalentTo(value);
        tree.GetValue(keyPrefix, AccountTreeIndexes.Balance).Should().BeEquivalentTo(value);
        tree.GetValue(keyPrefix, AccountTreeIndexes.Nonce).Should().BeEquivalentTo(value);
        tree.GetValue(keyPrefix, AccountTreeIndexes.CodeHash).Should().BeEquivalentTo(value);
        tree.GetValue(keyPrefix, AccountTreeIndexes.CodeSize).Should().BeEquivalentTo(value);

    }
    
    [Test]
    public void Set_Account_Value_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] keyPrefix = tree.GetTreeKeyPrefixAccount(TestItem.AddressA);
        byte[] version = {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
        };
        byte[] balance = {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2
        };
        byte[] nonce = {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        };
        byte[] codeHash = {
            197, 210, 70, 1, 134, 247, 35, 60, 146, 126, 125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39,
            59, 123, 250, 216, 4, 93, 133, 164, 112
        };
        byte[] codeSize = {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
        };
        
        tree.SetValue(keyPrefix,AccountTreeIndexes.Version, version);
        tree.SetValue(keyPrefix,AccountTreeIndexes.Balance, balance);
        tree.SetValue(keyPrefix,AccountTreeIndexes.Nonce, nonce);
        tree.SetValue(keyPrefix,AccountTreeIndexes.CodeHash, codeHash);
        tree.SetValue(keyPrefix,AccountTreeIndexes.CodeSize, codeSize);

        tree.GetValue(keyPrefix,AccountTreeIndexes.Version).Should().BeEquivalentTo(version);
        tree.GetValue(keyPrefix,AccountTreeIndexes.Balance).Should().BeEquivalentTo(balance);
        tree.GetValue(keyPrefix,AccountTreeIndexes.Nonce).Should().BeEquivalentTo(nonce);
        tree.GetValue(keyPrefix,AccountTreeIndexes.CodeHash).Should().BeEquivalentTo(codeHash);
        tree.GetValue(keyPrefix,AccountTreeIndexes.CodeSize).Should().BeEquivalentTo(codeSize);
        
    }
    
    [Test]
    public void Set_Account_Data_Type_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] keyPrefix = tree.GetTreeKeyPrefixAccount(TestItem.AddressA);
        UInt256 version = UInt256.Zero;
        UInt256 balance = new (2);
        UInt256 nonce = UInt256.Zero;
        Keccak codeHash = Keccak.OfAnEmptyString;
        UInt256 codeSize = UInt256.Zero;
        
        tree.SetValue(keyPrefix,AccountTreeIndexes.Version, version.ToBigEndian());
        tree.SetValue(keyPrefix,AccountTreeIndexes.Balance, balance.ToBigEndian());
        tree.SetValue(keyPrefix,AccountTreeIndexes.Nonce, nonce.ToBigEndian());
        tree.SetValue(keyPrefix,AccountTreeIndexes.CodeHash, codeHash.Bytes);
        tree.SetValue(keyPrefix,AccountTreeIndexes.CodeSize, codeSize.ToBigEndian());

        tree.GetValue(keyPrefix,AccountTreeIndexes.Version).Should().BeEquivalentTo(version.ToBigEndian());
        tree.GetValue(keyPrefix,AccountTreeIndexes.Balance).Should().BeEquivalentTo(balance.ToBigEndian());
        tree.GetValue(keyPrefix,AccountTreeIndexes.Nonce).Should().BeEquivalentTo(nonce.ToBigEndian());
        tree.GetValue(keyPrefix,AccountTreeIndexes.CodeHash).Should().BeEquivalentTo(codeHash.Bytes);
        tree.GetValue(keyPrefix,AccountTreeIndexes.CodeSize).Should().BeEquivalentTo(codeSize.ToBigEndian());
        
    }
    
    [Test]
    public void Set_Account_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] keyPrefix = tree.GetTreeKeyPrefixAccount(TestItem.AddressA);
        byte[] version = _account2.Version.ToBigEndian();
        byte[] balance = _account2.Balance.ToBigEndian();
        byte[] nonce = _account2.Nonce.ToBigEndian();
        byte[] codeHash = _account2.CodeHash.Bytes;
        byte[] codeSize = _account2.CodeSize.ToBigEndian();
        
        tree.SetValue(keyPrefix,AccountTreeIndexes.Version, version);
        tree.SetValue(keyPrefix,AccountTreeIndexes.Balance, balance);
        tree.SetValue(keyPrefix,AccountTreeIndexes.Nonce, nonce);
        tree.SetValue(keyPrefix,AccountTreeIndexes.CodeHash, codeHash);
        tree.SetValue(keyPrefix,AccountTreeIndexes.CodeSize, codeSize);
        
        tree.GetValue(keyPrefix,AccountTreeIndexes.Version).Should().BeEquivalentTo(version);
        tree.GetValue(keyPrefix,AccountTreeIndexes.Balance).Should().BeEquivalentTo(balance);
        tree.GetValue(keyPrefix,AccountTreeIndexes.Nonce).Should().BeEquivalentTo(nonce);
        tree.GetValue(keyPrefix,AccountTreeIndexes.CodeHash).Should().BeEquivalentTo(codeHash);
        tree.GetValue(keyPrefix,AccountTreeIndexes.CodeSize).Should().BeEquivalentTo(codeSize);
        
    }
    
    
}
