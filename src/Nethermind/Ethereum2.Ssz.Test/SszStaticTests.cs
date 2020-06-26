// /*
//  * Copyright (c) 2018 Demerzel Solutions Limited
//  * This file is part of the Nethermind library.
//  *
//  * The Nethermind library is free software: you can redistribute it and/or modify
//  * it under the terms of the GNU Lesser General Public License as published by
//  * the Free Software Foundation, either version 3 of the License, or
//  * (at your option) any later version.
//  *
//  * The Nethermind library is distributed in the hope that it will be useful,
//  * but WITHOUT ANY WARRANTY; without even the implied warranty of
//  * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  * GNU Lesser General Public License for more details.
//  *
//  * You should have received a copy of the GNU Lesser General Public License
//  * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//  */
//
// using System;
// using System.Collections.Generic;
// using System.IO;
// using System.Reflection;
// using Nethermind.Core.Extensions;
// using Nethermind.Core2.Containers;
// using Nethermind.Core2.Crypto;
// using Nethermind.Dirichlet.Numerics;
// using Nethermind.Merkleization;
// using Nethermind.Ssz;
// using NUnit.Framework;
// using YamlDotNet.RepresentationModel;
//
// namespace Ethereum2.Ssz.Test
// {
//     public class SszStaticTests
//     {
//         private static string ToSszStaticTestName(string category)
//         {
//             List<char> result = new List<char>();
//             foreach (char c in category)
//             {
//                 if (char.IsUpper(c))
//                 {
//                     result.Add('_');
//                     result.Add(char.ToLower(c));
//                 }
//                 else
//                 {
//                     result.Add(c);
//                 }
//             }
//
//             return "Ssz_static" + string.Join("", result);
//         }
//
//         [Test]
//         public void All_containers_tested()
//         {
//             foreach (string dirPath in Directory.GetDirectories("static"))
//             {
//                 string category = Path.GetFileName(dirPath);
//                 string testCaseName = ToSszStaticTestName(category);
//                 Assert.NotNull(GetType().GetMethod(testCaseName, BindingFlags.Public | BindingFlags.Instance), category);
//             }
//         }
//
//         [SetUp]
//         public void Setup()
//         {
//         }
//
//         [Test]
//         public void Ssz_static_aggregate_and_proof()
//         {
// //            RunStaticTests("AggregateAndProof");
//         }
//
//         [Test]
//         public void Ssz_static_attestation()
//         {
//             RunStaticTests("Attestation");
//         }
//
//         [Test]
//         public void Ssz_static_attestation_data()
//         {
//             RunStaticTests("AttestationData");
//         }
//
//         [Test]
//         public void Ssz_static_attester_slashing()
//         {
//             RunStaticTests("AttesterSlashing");
//         }
//
//         [Test]
//         public void Ssz_static_beacon_block()
//         {
//             RunStaticTests("BeaconBlock");
//         }
//
//         [Test]
//         public void Ssz_static_beacon_block_body()
//         {
//             RunStaticTests("BeaconBlockBody");
//         }
//
//         [Test]
//         public void Ssz_static_beacon_block_header()
//         {
//             RunStaticTests("BeaconBlockHeader");
//         }
//
//         [Test]
//         public void Ssz_static_beacon_state()
//         {
//             RunStaticTests("BeaconState");
//         }
//
//         [Test]
//         public void Ssz_static_checkpoint()
//         {
//             RunStaticTests("Checkpoint");
//         }
//
//         [Test]
//         public void Ssz_static_deposit()
//         {
//             RunStaticTests("Deposit");
//         }
//
//         [Test]
//         public void Ssz_static_deposit_data()
//         {
//             RunStaticTests("DepositData");
//         }
//
//         [Test]
//         public void Ssz_static_eth1_data()
//         {
//             RunStaticTests("Eth1Data");
//         }
//
//         [Test]
//         public void Ssz_static_fork()
//         {
//             RunStaticTests("Fork");
//         }
//
//         [Test]
//         public void Ssz_static_historical_batch()
//         {
//             RunStaticTests("HistoricalBatch");
//         }
//
//         [Test]
//         public void Ssz_static_indexed_attestation()
//         {
//             RunStaticTests("IndexedAttestation");
//         }
//
//         [Test]
//         public void Ssz_static_pending_attestation()
//         {
//             RunStaticTests("PendingAttestation");
//         }
//
//         [Test]
//         public void Ssz_static_proposer_slashing()
//         {
//             RunStaticTests("ProposerSlashing");
//         }
//
//         [Test]
//         public void Ssz_static_validator()
//         {
//             RunStaticTests("Validator");
//         }
//
//         [Test]
//         public void Ssz_static_voluntary_exit()
//         {
//             RunStaticTests("VoluntaryExit");
//         }
//
//         private static (YamlNode rootNode, YamlNodeType nodeType) LoadValue(string file)
//         {
//             using FileStream fileStream = File.OpenRead(file); // value.yaml
//             using var input = new StreamReader(fileStream);
//             var yaml = new YamlStream();
//             yaml.Load(input);
//
//             var rootNode = yaml.Documents[0].RootNode;
//             YamlNodeType nodeType = rootNode.NodeType;
//             return (rootNode, nodeType);
//         }
//         
//         private void RunStaticTests(string category)
//         {
//             string[] cases = Directory.GetDirectories(Path.Combine("static", category, "ssz_random"));
//             foreach (string testCaseDir in cases)
//             {
//                 byte[] serialized = File.ReadAllBytes(Path.Combine(testCaseDir, "serialized.ssz"));
//                 (YamlNode merkleRootYaml, _) = LoadValue(Path.Combine(testCaseDir, "roots.yaml")); // meta.yaml
//                 string expectedMerkleRootHex = ((YamlScalarNode) merkleRootYaml["root"]).Value;
//                 UInt256.CreateFromLittleEndian(out UInt256 expectedMerkleRoot, Bytes.FromHexString(expectedMerkleRootHex));
//                 Console.WriteLine(category + "." + testCaseDir);
//                 switch (category)
//                 {
//                     case "Attestation":
//                         TestAttestationSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "AttestationData":
//                         TestAttestationDataSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "AttesterSlashing":
//                         TestAttesterSlashingSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "BeaconBlock":
//                         TestBeaconBlockSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "BeaconBlockBody":
//                         TestBeaconBlockBodySsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "BeaconBlockHeader":
//                         TestBeaconBlockHeaderSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "BeaconState":
//                         TestBeaconStateSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "Checkpoint":
//                         TestCheckpointSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "Deposit":
//                         TestDepositSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "DepositData":
//                         TestDepositDataSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "Eth1Data":
//                         TestEth1DataSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "Fork":
//                         TestForkSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "HistoricalBatch":
//                         TestHistoricBatchSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "IndexedAttestation":
//                         TestIndexedAttestationSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "PendingAttestation":
//                         TestPendingAttestationSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "ProposerSlashing":
//                         TestProposerSlashingSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "Validator":
//                         TestValidatorSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     case "VoluntaryExit":
//                         TestVoluntaryExitSsz(serialized, expectedMerkleRoot, testCaseDir);
//                         break;
//                     default:
//                         throw new InvalidOperationException($"Unknown test case {category}");
//                 }
//             }
//         }
//
//         private static void TestAttestationSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             Attestation container = Nethermind.Ssz.Ssz.DecodeAttestation(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestAttestationDataSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             AttestationData container = Nethermind.Ssz.Ssz.DecodeAttestationData(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestAttesterSlashingSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             AttesterSlashing container = Nethermind.Ssz.Ssz.DecodeAttesterSlashing(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestBeaconBlockSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             BeaconBlock container = Nethermind.Ssz.Ssz.DecodeBeaconBlock(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestBeaconBlockBodySsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             BeaconBlockBody container = Nethermind.Ssz.Ssz.DecodeBeaconBlockBody(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestBeaconBlockHeaderSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             BeaconBlockHeader container = Nethermind.Ssz.Ssz.DecodeBeaconBlockHeader(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestBeaconStateSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             BeaconState container = Nethermind.Ssz.Ssz.DecodeBeaconState(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestCheckpointSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             Checkpoint container = Nethermind.Ssz.Ssz.DecodeCheckpoint(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestDepositSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             Deposit? container = Nethermind.Ssz.Ssz.DecodeDeposit(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestDepositDataSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             DepositData container = Nethermind.Ssz.Ssz.DecodeDepositData(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestEth1DataSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             Eth1Data? container = Nethermind.Ssz.Ssz.DecodeEth1Data(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestForkSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             Fork? container = Nethermind.Ssz.Ssz.DecodeFork(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestHistoricBatchSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             HistoricalBatch? container = Nethermind.Ssz.Ssz.DecodeHistoricalBatch(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestIndexedAttestationSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             IndexedAttestation? container = Nethermind.Ssz.Ssz.DecodeIndexedAttestation(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestPendingAttestationSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             PendingAttestation? container = Nethermind.Ssz.Ssz.DecodePendingAttestation(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestProposerSlashingSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             ProposerSlashing? container = Nethermind.Ssz.Ssz.DecodeProposerSlashing(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestValidatorSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             Validator? container = Nethermind.Ssz.Ssz.DecodeValidator(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//
//         private static void TestVoluntaryExitSsz(byte[] serialized, UInt256 expectedMerkleRoot, string testCaseDir)
//         {
//             VoluntaryExit? container = Nethermind.Ssz.Ssz.DecodeVoluntaryExit(serialized);
//             byte[] again = new byte[serialized.Length];
//             Nethermind.Ssz.Ssz.Encode(again, container);
//             Assert.AreEqual(serialized.ToHexString(), again.ToHexString(), testCaseDir);
//             
//             Nethermind.Ssz.Merkle.Ize(out UInt256 root, container);
//             Assert.AreEqual(expectedMerkleRoot, root);
//         }
//     }
// }