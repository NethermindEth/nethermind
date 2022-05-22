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

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    private readonly Account account = Build.An.Account.WithBalance(2).TestObject;

    [SetUp]
    public void Setup()
    {
        Trie.Metrics.TreeNodeHashCalculations = 0;
        Trie.Metrics.TreeNodeRlpDecodings = 0;
        Trie.Metrics.TreeNodeRlpEncodings = 0;
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
        byte[] version = account.Version.ToBigEndian();
        byte[] balance = account.Balance.ToBigEndian();
        byte[] nonce = account.Nonce.ToBigEndian();
        byte[] codeHash = account.CodeHash.Bytes;
        byte[] codeSize = account.CodeSize.ToBigEndian();
        
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
    public void Set_Account_With_Code()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] code = {1, 2, 3, 4};
        tree.SetCode(TestItem.AddressA, code);

        byte[] key =tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 0);
        byte[] value = tree.GetValue(key);
        value.Should().NotBeNull();
        value.Slice(0, 5).Should().BeEquivalentTo(new byte[] {0, 1, 2, 3, 4}); 
        value.Slice(5, 27).Should().BeEquivalentTo(new byte[27]);
        
        key =tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 1);
        value = tree.GetValue(key);

        value.Should().BeNull();
    }
    
    [Test]
    public void Set_Account_With_Code_Push_Opcodes()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] code = {97, 1, 2, 3, 4};
        tree.SetCode(TestItem.AddressA, code);

        byte[] key =tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 0);
        byte[] value = tree.GetValue(key);
        value.Should().NotBeNull();
        value.Slice(0, 6).Should().BeEquivalentTo(new byte[] {0, 97, 1, 2, 3, 4}); 
        value.Slice(6, 26).Should().BeEquivalentTo(new byte[26]);
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 1);
        value = tree.GetValue(key);

        value.Should().BeNull();
        
        byte[] code2 =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23,
            24, 25, 26, 27, 28, 100, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45
        };
        tree.SetCode(TestItem.AddressA, code2);
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 0);
        value = tree.GetValue(key);

        byte[] firstCodeChunk =
        {
            0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            28, 100, 30
        };
        byte[] secondCodeChunk =
        {
            4, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45
        };

        value.Should().BeEquivalentTo(firstCodeChunk);
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 1);
        value = tree.GetValue(key);
        
        value.Slice(0, 16).Should().BeEquivalentTo(secondCodeChunk); 
        value.Slice(16, 16).Should().BeEquivalentTo(new byte[16]);

    }

    [Test]
    public void Set_Code_Edge_Cases_1()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] code2 =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            28, 29, 127, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,
            54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65
        };
        tree.SetCode(TestItem.AddressA, code2);

        byte[] firstCodeChunk =
        {
            0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            28, 29, 127
        };
        byte[] secondCodeChunk =
        {
            31, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,
            54, 55, 56, 57, 58, 59, 60, 61
        };
        byte[] thirdCodeChunk =
        {
            1, 62, 63, 64, 65
        };
        
        byte[] key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 0);
        byte[] value = tree.GetValue(key);
        value.Should().BeEquivalentTo(firstCodeChunk);
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 1);
        value = tree.GetValue(key);
        value.Should().BeEquivalentTo(secondCodeChunk); 
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 2);
        value = tree.GetValue(key);
        value.Slice(0, 5).Should().BeEquivalentTo(thirdCodeChunk); 
    }
    
    [Test]
    public void Set_Code_Edge_Cases_2()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] code2 =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            28, 29, 126, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,
            54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65
        };
        tree.SetCode(TestItem.AddressA, code2);

        byte[] firstCodeChunk =
        {
            0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            28, 29, 126
        };
        byte[] secondCodeChunk =
        {
            31, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,
            54, 55, 56, 57, 58, 59, 60, 61
        };
        byte[] thirdCodeChunk =
        {
            0, 62, 63, 64, 65
        };
        
        byte[] key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 0);
        byte[] value = tree.GetValue(key);
        value.Should().BeEquivalentTo(firstCodeChunk);
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 1);
        value = tree.GetValue(key);
        value.Should().BeEquivalentTo(secondCodeChunk); 
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 2);
        value = tree.GetValue(key);
        value.Slice(0, 5).Should().BeEquivalentTo(thirdCodeChunk); 
    }
    
    [Test]
    public void Set_Code_Edge_Cases_3()
    {
        VerkleStateTree tree = new(LimboLogs.Instance);
        byte[] code2 =
        {
            95, 1, 96, 3, 4, 97, 6, 7, 8, 98, 10, 11, 12, 13, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 112, 113,
            114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,
            54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65
        };
        tree.SetCode(TestItem.AddressA, code2);

        byte[] firstCodeChunk =
        {
            0, 95, 1, 96, 3, 4, 97, 6, 7, 8, 98, 10, 11, 12, 13, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 112, 113,
            114, 115, 116
        };
        byte[] secondCodeChunk =
        {
            19, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,
            54, 55, 56, 57, 58, 59, 60, 61
        };
        byte[] thirdCodeChunk =
        {
            0, 62, 63, 64, 65
        };
        
        byte[] key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 0);
        byte[] value = tree.GetValue(key);
        value.Should().BeEquivalentTo(firstCodeChunk);
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 1);
        value = tree.GetValue(key);
        value.Should().BeEquivalentTo(secondCodeChunk); 
        
        key = tree.GetTreeKeyForCodeChunk(TestItem.AddressA, 2);
        value = tree.GetValue(key);
        value.Slice(0, 5).Should().BeEquivalentTo(thirdCodeChunk); 
    }

}
