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
    
    [Test]
    public void Get_Account_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
        Assert.AreEqual(treeKeys.Length, 5);
        Assert.AreEqual(treeKeys[AccountTreeIndexes.Version], treeKeyVersion);
        Assert.AreEqual(treeKeys[AccountTreeIndexes.Balance], treeKeyBalance);
        Assert.AreEqual(treeKeys[AccountTreeIndexes.Nonce], treeKeyNonce);
        Assert.AreEqual(treeKeys[AccountTreeIndexes.CodeHash], treeKeyCodeKeccak);
        Assert.AreEqual(treeKeys[AccountTreeIndexes.CodeSize], treeKeyCodeSize);
    }
    
    [Test]
    public void Set_Get_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
        
        byte[] value =  {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
        };
        
        tree.SetValue(treeKeys[AccountTreeIndexes.Version], value);
        tree.SetValue(treeKeys[AccountTreeIndexes.Balance], value);
        tree.SetValue(treeKeys[AccountTreeIndexes.Nonce], value);
        tree.SetValue(treeKeys[AccountTreeIndexes.CodeHash], value);
        tree.SetValue(treeKeys[AccountTreeIndexes.CodeSize], value);

        tree.GetValue(treeKeys[AccountTreeIndexes.Version]);
        tree.GetValue(treeKeys[AccountTreeIndexes.Balance]);
        tree.GetValue(treeKeys[AccountTreeIndexes.Nonce]);
        tree.GetValue(treeKeys[AccountTreeIndexes.CodeHash]);
        tree.GetValue(treeKeys[AccountTreeIndexes.CodeSize]);

    }
    
    [Test]
    public void Set_Account_Value_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
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
        
        tree.SetValue(treeKeys[AccountTreeIndexes.Version], version);
        tree.SetValue(treeKeys[AccountTreeIndexes.Balance], balance);
        tree.SetValue(treeKeys[AccountTreeIndexes.Nonce], nonce);
        tree.SetValue(treeKeys[AccountTreeIndexes.CodeHash], codeHash);
        tree.SetValue(treeKeys[AccountTreeIndexes.CodeSize], codeSize);

        tree.GetValue(treeKeys[AccountTreeIndexes.Version]);
        tree.GetValue(treeKeys[AccountTreeIndexes.Balance]);
        tree.GetValue(treeKeys[AccountTreeIndexes.Nonce]);
        tree.GetValue(treeKeys[AccountTreeIndexes.CodeHash]);
        tree.GetValue(treeKeys[AccountTreeIndexes.CodeSize]);
        
    }
    
    [Test]
    public void Set_Account_Data_Type_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
        UInt256 version = UInt256.Zero;
        UInt256 balance = new (2);
        UInt256 nonce = UInt256.Zero;
        Keccak codeHash = Keccak.OfAnEmptyString;
        UInt256 codeSize = UInt256.Zero;
        
        tree.SetValue(treeKeys[AccountTreeIndexes.Version], version.ToBigEndian());
        tree.SetValue(treeKeys[AccountTreeIndexes.Balance], balance.ToBigEndian());
        tree.SetValue(treeKeys[AccountTreeIndexes.Nonce], nonce.ToBigEndian());
        tree.SetValue(treeKeys[AccountTreeIndexes.CodeHash], codeHash.Bytes);
        tree.SetValue(treeKeys[AccountTreeIndexes.CodeSize], codeSize.ToBigEndian());

        tree.GetValue(treeKeys[AccountTreeIndexes.Version]);
        tree.GetValue(treeKeys[AccountTreeIndexes.Balance]);
        tree.GetValue(treeKeys[AccountTreeIndexes.Nonce]);
        tree.GetValue(treeKeys[AccountTreeIndexes.CodeHash]);
        tree.GetValue(treeKeys[AccountTreeIndexes.CodeSize]);
        
    }
    
    [Test]
    public void Set_Account_Keys()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
        byte[] version = _account2.Version.ToBigEndian();
        byte[] balance = _account2.Balance.ToBigEndian();
        byte[] nonce = _account2.Nonce.ToBigEndian();
        byte[] codeHash = _account2.CodeHash.Bytes;
        byte[] codeSize = _account2.CodeSize.ToBigEndian();
        
        tree.SetValue(treeKeys[AccountTreeIndexes.Version], version);
        tree.SetValue(treeKeys[AccountTreeIndexes.Balance], balance);
        tree.SetValue(treeKeys[AccountTreeIndexes.Nonce], nonce);
        tree.SetValue(treeKeys[AccountTreeIndexes.CodeHash], codeHash);
        tree.SetValue(treeKeys[AccountTreeIndexes.CodeSize], codeSize);
        
        tree.GetValue(treeKeys[AccountTreeIndexes.Version]);
        tree.GetValue(treeKeys[AccountTreeIndexes.Balance]);
        tree.GetValue(treeKeys[AccountTreeIndexes.Nonce]);
        tree.GetValue(treeKeys[AccountTreeIndexes.CodeHash]);
        tree.GetValue(treeKeys[AccountTreeIndexes.CodeSize]);
        
    }
    
    
}
