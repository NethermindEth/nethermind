// // SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// // SPDX-License-Identifier: LGPL-3.0-only
//
// using System;
// using FluentAssertions;
// using Nethermind.Core;
// using Nethermind.Core.Crypto;
// using Nethermind.Core.Test.Builders;
// using Nethermind.Core.Verkle;
// using Nethermind.Db;
// using Nethermind.Evm.CodeAnalysis;
// using Nethermind.Int256;
// using Nethermind.Logging;
// using Nethermind.Specs;
// using Nethermind.Specs.Forks;
// using Nethermind.State;
// using NUnit.Framework;
//
// namespace Nethermind.Evm.Test;
//
// [TestFixture]
// public class CodeTests
// {
//     [Test]
//     public void ICodeTests()
//     {
//         TestSpecProvider specProvider = new (Prague.Instance);
//         VerkleStateTree tree = TestItem.Tree.GetVerkleStateTree(null);
//         MemDb codeDb = new ();
//         VerkleWorldState worldState = new (tree, codeDb, LimboLogs.Instance);
//
//         byte[] code = new byte[2000];
//         TestItem.Random.NextBytes(code);
//
//         worldState.CreateAccount(TestItem.AddressA, 200000);
//         worldState.CreateAccount(TestItem.AddressB, 200000);
//         worldState.Commit(specProvider.SpecToReturn);
//         worldState.InsertCode(TestItem.AddressA, code, specProvider.SpecToReturn, false);
//         worldState.Commit(specProvider.SpecToReturn);
//         worldState.CommitTree(0);
//
//         ByteCode byteCode = new (code);
//         VerkleCode verkleCode = new (worldState, TestItem.AddressA);
//
//         Assert.IsTrue(byteCode.Length == verkleCode.Length);
//
//         byteCode.MachineCode.Span.ToArray().Should().BeEquivalentTo(verkleCode.MachineCode.Span.ToArray());
//
//         for (int i = 0; i < code.Length; i++) AssetEqualSlice(byteCode, verkleCode, i, code.Length);
//         for (int i = 0; i < code.Length; i++) AssetEqualZeroPaddedSpan(byteCode, verkleCode, i, code.Length);
//     }
//
//     [Test]
//     public void ICodeTestsWithVerkleChunks()
//     {
//         TestSpecProvider specProvider = new (Prague.Instance);
//         VerkleStateTree tree = TestItem.Tree.GetVerkleStateTree(null);
//         MemDb codeDb = new ();
//         VerkleWorldState worldState = new (tree, codeDb, LimboLogs.Instance);
//
//         byte[] code = new byte[2000];
//         TestItem.Random.NextBytes(code);
//
//         worldState.CreateAccount(TestItem.AddressA, 200000);
//         worldState.CreateAccount(TestItem.AddressB, 200000);
//         worldState.Commit(specProvider.SpecToReturn);
//
//         {
//             UInt256 chunkId = 0;
//             var codeEnumerator = new CodeChunkEnumerator(code);
//             bool skip = true;
//             while (codeEnumerator.TryGetNextChunk(out byte[] chunk))
//             {
//                 if (skip)
//                 {
//                     skip = false;
//                     chunkId += 1;
//                     continue;
//                 }
//
//                 skip = true;
//                 Hash256? key = AccountHeader.GetTreeKeyForCodeChunk(TestItem.AddressA.Bytes, chunkId);
//                 tree.Insert(key, chunk);
//                 chunkId += 1;
//             }
//
//             tree.Set(TestItem.AddressA, new Account(0, 200000, Keccak.EmptyTreeHash, Keccak.MaxValue){CodeSize = 2000});
//             tree.Commit();
//         }
//
//         worldState.CommitTree(0);
//
//         ByteCode byteCode = new (code);
//         VerkleCode verkleCode = new (worldState, TestItem.AddressA);
//
//         Assert.IsTrue(byteCode.Length == verkleCode.Length);
//
//
//         byteCode.MachineCode.Span.ToArray().Should().BeEquivalentTo(verkleCode.MachineCode.Span.ToArray());
//
//         for (int i = 0; i < code.Length; i++) AssetEqualSlice(byteCode, verkleCode, i, code.Length);
//         for (int i = 0; i < code.Length; i++) AssetEqualZeroPaddedSpan(byteCode, verkleCode, i, code.Length);
//     }
//
//     private void AssetEqualSlice(ByteCode byteCode, VerkleCode verkleCode, int start, int length)
//     {
//         ReadOnlySpan<byte> byteCodeSlice = byteCode.AsSpan().Slice(start, Math.Min(length - start, length));
//         ReadOnlySpan<byte> verkleCodeSlice = verkleCode.AsSpan().Slice(start, Math.Min(length - start, length));
//         byteCodeSlice.ToArray().Should().BeEquivalentTo(verkleCodeSlice.ToArray());
//     }
//
//     private void AssetEqualZeroPaddedSpan(ByteCode byteCode, VerkleCode verkleCode, int start, int length)
//     {
//         ZeroPaddedSpan byteCodeSlice = byteCode.AsSpan().SliceWithZeroPadding((UInt256)start, length);
//         ZeroPaddedSpan verkleCodeSlice = verkleCode.AsSpan().SliceWithZeroPadding((UInt256)start, length);
//         Assert.IsTrue(byteCodeSlice.Length == verkleCodeSlice.Length);
//         Assert.IsTrue(byteCodeSlice.PaddingLength == verkleCodeSlice.PaddingLength);
//         Assert.IsTrue(byteCodeSlice.PadDirection == verkleCodeSlice.PadDirection);
//         Assert.IsTrue(byteCodeSlice.Span.SequenceEqual(verkleCodeSlice.Span));
//     }
// }
