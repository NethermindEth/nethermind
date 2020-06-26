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
// using System.Buffers.Binary;
// using System.Collections.Generic;
// using System.IO;
// using System.Linq;
// using System.Text.RegularExpressions;
// using Nethermind.Core.Extensions;
// using Nethermind.Dirichlet.Numerics;
// using Nethermind.Merkleization;
// using Nethermind.Ssz;
// using NUnit.Framework;
// using YamlDotNet.RepresentationModel;
//
// namespace Ethereum2.Ssz.Test
// {
//     public class SszGenericTests
//     {
//         [SetUp]
//         public void Setup()
//         {
//         }
//
//         [Test]
//         public void Ssz_basic_vector()
//         {
//             Assert.True(RunGenericSszTests("basic_vector"));
//         }
//
//         [Test]
//         public void Ssz_bitvector()
//         {
//             Assert.True(RunGenericSszTests("bitvector"));
//         }
//
//         [Test]
//         public void Ssz_bitlist()
//         {
//             Assert.True(RunGenericSszTests("bitlist"));
//         }
//
//         [Test]
//         public void Ssz_boolean()
//         {
//             Assert.True(RunGenericSszTests("boolean"));
//         }
//
//         [Test]
//         public void Ssz_uints()
//         {
//             Assert.True(RunGenericSszTests("uints"));
//         }
//
//         [Test]
//         public void Ssz_containers()
//         {
//             Assert.True(RunGenericSszTests("containers"));
//         }
//
//         private static bool RunGenericSszTests(string category)
//         {
//             bool success = true;
//             string[] valid = Directory.GetDirectories(Path.Combine("generic", category, "valid"));
//             string[] invalid = Directory.GetDirectories(Path.Combine("generic", category, "invalid"));
//
//             foreach (string validDir in valid)
//             {
//                 TestContext.Out.WriteLine(validDir);
//                 string[] files = Directory.GetFiles(validDir);
//                 (YamlNode valueNode, YamlNodeType valueType) = LoadValue(Path.Combine(validDir, "value.yaml"));
//                 (YamlNode merkleRootYaml, _) = LoadValue(Path.Combine(validDir, "meta.yaml"));
//                 UInt256.CreateFromLittleEndian(out UInt256 expectedMerkleRoot, Bytes.FromHexString(((YamlScalarNode) merkleRootYaml["root"]).Value));
//
//                 Span<byte> output = null;
//                 Span<byte> ssz = File.ReadAllBytes(Path.Combine(validDir, "serialized.ssz"));
//
//                 if (valueType == YamlNodeType.Sequence)
//                 {
//                     YamlSequenceNode sequenceNode = (YamlSequenceNode) valueNode;
//                     if (validDir.Contains("bool"))
//                     {
//                         bool[] value = sequenceNode.Children.Cast<YamlScalarNode>().Select(sn => bool.Parse(sn?.Value ?? "false")).ToArray();
//                         bool[] valueFromSsz = Nethermind.Ssz.Ssz.DecodeBools(ssz).ToArray();
//                         output = new byte[value.Length];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//                         byte[] clone = output.ToArray();
//                         Nethermind.Ssz.Ssz.Encode(output, valueFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                         
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint256"))
//                     {
//                         UInt256[] value = sequenceNode.Children.Cast<YamlScalarNode>().Select(sn => UInt256.Parse(sn.Value)).ToArray();
//                         UInt256[] valueFromSsz = Nethermind.Ssz.Ssz.DecodeUInts256(ssz);
//                         output = new byte[value.Length * 32];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//                         byte[] clone = output.ToArray();
//                         Nethermind.Ssz.Ssz.Encode(output, valueFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint128"))
//                     {
//                         UInt128[] value = sequenceNode.Children.Cast<YamlScalarNode>().Select(sn => UInt128.Parse(sn.Value)).ToArray();
//                         UInt128[] valueFromSsz = Nethermind.Ssz.Ssz.DecodeUInts128(ssz);
//                         output = new byte[value.Length * 16];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//                         byte[] clone = output.ToArray();
//                         Nethermind.Ssz.Ssz.Encode(output, valueFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                         
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint64"))
//                     {
//                         ulong[] value = sequenceNode.Children.Cast<YamlScalarNode>().Select(sn => ulong.Parse(sn.Value ?? ulong.MinValue.ToString())).ToArray();
//                         ulong[] valueFromSsz = Nethermind.Ssz.Ssz.DecodeULongs(ssz).ToArray();
//                         output = new byte[value.Length * 8];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//                         byte[] clone = output.ToArray();
//                         Nethermind.Ssz.Ssz.Encode(output, valueFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                         
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint32"))
//                     {
//                         uint[] value = sequenceNode.Children.Cast<YamlScalarNode>().Select(sn => uint.Parse(sn.Value ?? uint.MinValue.ToString())).ToArray();
//                         uint[] valueFromSsz = Nethermind.Ssz.Ssz.DecodeUInts(ssz).ToArray();
//                         output = new byte[value.Length * 4];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//                         byte[] clone = output.ToArray();
//                         Nethermind.Ssz.Ssz.Encode(output, valueFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                         
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint16"))
//                     {
//                         ushort[] value = sequenceNode.Children.Cast<YamlScalarNode>().Select(sn => ushort.Parse(sn.Value ?? ushort.MinValue.ToString())).ToArray();
//                         ushort[] valueFromSsz = Nethermind.Ssz.Ssz.DecodeUShorts(ssz).ToArray();
//                         output = new byte[value.Length * 2];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//                         byte[] clone = output.ToArray();
//                         Nethermind.Ssz.Ssz.Encode(output, valueFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                         
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint8"))
//                     {
//                         byte[] value = sequenceNode.Children.Cast<YamlScalarNode>().Select(sn => byte.Parse(sn.Value ?? byte.MinValue.ToString())).ToArray();
//                         byte[] valueFromSsz = Nethermind.Ssz.Ssz.DecodeBytes(ssz).ToArray();
//                         output = new byte[value.Length];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//                         byte[] clone = output.ToArray();
//                         Nethermind.Ssz.Ssz.Encode(output, valueFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                         
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                 }
//                 else if (valueType == YamlNodeType.Scalar)
//                 {
//                     if (validDir.Contains("bitvec") || validDir.Contains("bitlist"))
//                     {
//                         uint limit = 0;
//                         Match match = Regex.Match(validDir, "bitlist_(\\d+)", RegexOptions.IgnoreCase);
//                         if (match.Success)
//                         {
//                             limit = (uint.Parse(match.Groups[1].Value) + 255) / 256;
//                         }
//
//                         byte[] value = Bytes.FromHexString(((YamlScalarNode) valueNode).Value);
//                         byte[] valueFromSsz = Nethermind.Ssz.Ssz.DecodeBytes(ssz).ToArray();
//                         output = new byte[value.Length];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//                         byte[] clone = output.ToArray();
//                         Nethermind.Ssz.Ssz.Encode(output, valueFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//
//
//                         UInt256 root;
//                         if (validDir.Contains("bitvec"))
//                         {
//                             Nethermind.Ssz.Merkle.Ize(out root, valueFromSsz);
//                         }
//                         else
//                         {
//                             Nethermind.Ssz.Merkle.IzeBits(out root, valueFromSsz, limit);
//                         }
//
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("boolean"))
//                     {
//                         string yamlValue = ((YamlScalarNode) valueNode)?.Value;
//                         bool? value = yamlValue is null ? null : (bool?)bool.Parse(yamlValue);
//                         bool? valueFromSsz = Nethermind.Ssz.Ssz.DecodeBool(ssz);
//                         Assert.AreEqual(value, valueFromSsz);
//                         output = new byte[1];
//                         output[0] = Nethermind.Ssz.Ssz.Encode(value ?? false);
//
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz ?? false);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint_256"))
//                     {
//                         UInt256 value = UInt256.Parse(((YamlScalarNode) valueNode).Value);
//                         UInt256 valueFromSsz = Nethermind.Ssz.Ssz.DecodeUInt256(ssz);
//                         Assert.AreEqual(value, valueFromSsz);
//
//                         output = new byte[32];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint_128"))
//                     {
//                         UInt128 value = UInt128.Parse(((YamlScalarNode) valueNode).Value);
//                         UInt128 valueFromSsz = Nethermind.Ssz.Ssz.DecodeUInt128(ssz);
//                         Assert.AreEqual(value, valueFromSsz);
//
//                         output = new byte[16];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint_64"))
//                     {
//                         ulong value = ulong.Parse(((YamlScalarNode) valueNode).Value);
//                         ulong valueFromSsz = Nethermind.Ssz.Ssz.DecodeULong(ssz);
//                         Assert.AreEqual(value, valueFromSsz);
//
//                         output = new byte[8];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint_32"))
//                     {
//                         uint value = uint.Parse(((YamlScalarNode) valueNode).Value);
//                         uint valueFromSsz = Nethermind.Ssz.Ssz.DecodeUInt(ssz);
//                         Assert.AreEqual(value, valueFromSsz);
//
//                         output = new byte[4];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint_16"))
//                     {
//                         ushort value = ushort.Parse(((YamlScalarNode) valueNode).Value);
//                         ushort valueFromSsz = Nethermind.Ssz.Ssz.DecodeUShort(ssz);
//                         Assert.AreEqual(value, valueFromSsz);
//
//                         output = new byte[2];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                     else if (validDir.Contains("uint_8"))
//                     {
//                         byte value = byte.Parse(((YamlScalarNode) valueNode).Value);
//                         byte valueFromSsz = Nethermind.Ssz.Ssz.DecodeByte(ssz);
//                         Assert.AreEqual(value, valueFromSsz);
//
//                         output = new byte[1];
//                         Nethermind.Ssz.Ssz.Encode(output, value);
//
//                         Nethermind.Ssz.Merkle.Ize(out UInt256 root, valueFromSsz);
//                         Assert.AreEqual(expectedMerkleRoot, root);
//                     }
//                 }
//                 else if (valueType == YamlNodeType.Mapping)
//                 {
//                     var mappingNode = (YamlMappingNode) valueNode;
//                     if (validDir.Contains("BitsStruct"))
//                     {
//                         BitsStruct testStruct = ParseBitsStruct(mappingNode);
//                         BitsStruct testStructFromSsz = DecodeBitsStruct(ssz);
//                         output = new byte[13];
//                         Encode(output, testStruct);
//                         byte[] clone = output.ToArray();
//                         Encode(output, testStructFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                     }
//                     else if (validDir.Contains("SmallTestStruct"))
//                     {
//                         SmallTestStruct testStruct = ParseSmallTestStruct(mappingNode);
//                         SmallTestStruct testStructFromSsz = DecodeSmallTestStruct(ssz);
//                         output = new byte[4];
//                         Encode(output, testStruct);
//                         byte[] clone = output.ToArray();
//                         Encode(output, testStructFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                     }
//                     else if (validDir.Contains("SingleFieldTestStruct"))
//                     {
//                         SingleFieldTestStruct testStruct = new SingleFieldTestStruct();
//                         SingleFieldTestStruct testStructFromSsz = DecodeSingleFieldTestStruct(ssz);
//                         testStruct.A = byte.Parse(((YamlScalarNode) mappingNode.Children["A"]).Value);
//                         output = new byte[1];
//                         Encode(output, testStruct);
//                         byte[] clone = output.ToArray();
//                         Encode(output, testStructFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                     }
//                     else if (validDir.Contains("VarTestStruct"))
//                     {
//                         VarTestStruct testStruct = ParseVarTestStruct(mappingNode);
//                         VarTestStruct testStructFromSsz = DecodeVarTestStruct(ssz);
//                         output = new byte[7 + testStruct.B.Length * 2];
//                         Encode(output, testStruct);
//                         byte[] clone = output.ToArray();
//                         Encode(output, testStructFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                     }
//                     else if (validDir.Contains("FixedTestStruct"))
//                     {
//                         FixedTestStruct testStruct = ParseFixedTestStruct(mappingNode);
//                         FixedTestStruct testStructFromSsz = DecodeFixTestStruct(ssz);
//                         output = new byte[13];
//                         Encode(output, testStruct);
//                         byte[] clone = output.ToArray();
//                         Encode(output, testStructFromSsz);
//                         Assert.AreEqual(clone.ToHexString(), output.ToHexString(), "ssz");
//                     }
//                     else if (validDir.Contains("ComplexTestStruct"))
//                     {
//                         ComplexTestStruct testStruct = ParseComplexTestStruct(mappingNode);
//                         output = new byte[8236];
//                         Encode(ref output, testStruct);
//                     }
//                 }
//
//                 if (ssz.ToHexString() != output.ToHexString())
//                 {
//                     TestContext.Out.WriteLine($"  expected {ssz.ToHexString()}");
//                     TestContext.Out.WriteLine($"  actual   {output.ToHexString()}");
//                     success = false;
//                 }
//             }
//
//             return success;
//         }
//
//         private static ComplexTestStruct ParseComplexTestStruct(YamlMappingNode mappingNode)
//         {
//             ComplexTestStruct testStruct = new ComplexTestStruct();
//             testStruct.A = ushort.Parse(((YamlScalarNode) mappingNode.Children["A"]).Value);
//             testStruct.B = ((YamlSequenceNode) mappingNode.Children["B"]).Children.Cast<YamlScalarNode>().Select(c => ushort.Parse(c.Value)).ToArray();
//             testStruct.C = byte.Parse(((YamlScalarNode) mappingNode.Children["C"]).Value);
//             testStruct.D = Bytes.FromHexString(((YamlScalarNode) mappingNode.Children["D"]).Value);
//
//             YamlMappingNode varNode = (YamlMappingNode) mappingNode.Children["E"];
//             testStruct.E = ParseVarTestStruct(varNode);
//             testStruct.F = new List<FixedTestStruct>();
//             foreach (var yamlNode in ((YamlSequenceNode) mappingNode.Children["F"]).Children)
//             {
//                 var child = (YamlMappingNode) yamlNode;
//                 testStruct.F.Add(ParseFixedTestStruct(child));
//             }
//
//             testStruct.G = new List<VarTestStruct>();
//             foreach (var yamlNode in ((YamlSequenceNode) mappingNode.Children["G"]).Children)
//             {
//                 var child = (YamlMappingNode) yamlNode;
//                 testStruct.G.Add(ParseVarTestStruct(child));
//             }
//
//
//             return testStruct;
//         }
//
//         private static SmallTestStruct ParseSmallTestStruct(YamlMappingNode mappingNode)
//         {
//             SmallTestStruct testStruct = new SmallTestStruct();
//             testStruct.A = ushort.Parse(((YamlScalarNode) mappingNode.Children["A"]).Value);
//             testStruct.B = ushort.Parse(((YamlScalarNode) mappingNode.Children["B"]).Value);
//             return testStruct;
//         }
//
//         private static BitsStruct ParseBitsStruct(YamlMappingNode mappingNode)
//         {
//             BitsStruct testStruct = new BitsStruct();
//             testStruct.A = Bytes.FromHexString(((YamlScalarNode) mappingNode.Children["A"]).Value);
//             testStruct.B = Bytes.FromHexString(((YamlScalarNode) mappingNode.Children["B"]).Value);
//             testStruct.C = Bytes.FromHexString(((YamlScalarNode) mappingNode.Children["C"]).Value);
//             testStruct.D = Bytes.FromHexString(((YamlScalarNode) mappingNode.Children["D"]).Value);
//             testStruct.E = Bytes.FromHexString(((YamlScalarNode) mappingNode.Children["E"]).Value);
//             return testStruct;
//         }
//
//         private static FixedTestStruct ParseFixedTestStruct(YamlMappingNode mappingNode)
//         {
//             FixedTestStruct testStruct = new FixedTestStruct();
//             testStruct.A = byte.Parse(((YamlScalarNode) mappingNode.Children["A"]).Value);
//             testStruct.B = ulong.Parse(((YamlScalarNode) mappingNode.Children["B"]).Value);
//             testStruct.C = uint.Parse(((YamlScalarNode) mappingNode.Children["C"]).Value);
//             return testStruct;
//         }
//
//         private static VarTestStruct ParseVarTestStruct(YamlMappingNode varNode)
//         {
//             VarTestStruct varStruct = new VarTestStruct();
//             varStruct.A = ushort.Parse(((YamlScalarNode) (varNode).Children["A"]).Value);
//             varStruct.B = ((YamlSequenceNode) (varNode).Children["B"]).Children.Cast<YamlScalarNode>().Select(c => ushort.Parse(c.Value)).ToArray();
//             varStruct.C = byte.Parse(((YamlScalarNode) (varNode).Children["C"]).Value);
//             return varStruct;
//         }
//
//         private static void Encode(Span<byte> span, BitsStruct testStruct)
//         {
//             Nethermind.Ssz.Ssz.Encode(span.Slice(0, 4), 11);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(4, 1), testStruct.B);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(5, 1), testStruct.C);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(6, 4), 12);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(10, 1), testStruct.E);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(11, 1), testStruct.A);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(12, 1), testStruct.D);
//         }
//
//         private static BitsStruct DecodeBitsStruct(Span<byte> span)
//         {
//             BitsStruct testStruct = new BitsStruct();
//             uint offset0 = Nethermind.Ssz.Ssz.DecodeUInt(span.Slice(0, 4));
//             uint offset1 = Nethermind.Ssz.Ssz.DecodeUInt(span.Slice(6, 4));
//             testStruct.A = Nethermind.Ssz.Ssz.DecodeBytes(span.Slice((int) offset0, (int) (offset1 - offset0))).ToArray();
//             testStruct.B = Nethermind.Ssz.Ssz.DecodeBytes(span.Slice(4, 1)).ToArray();
//             testStruct.C = Nethermind.Ssz.Ssz.DecodeBytes(span.Slice(5, 1)).ToArray();
//             testStruct.D = Nethermind.Ssz.Ssz.DecodeBytes(span.Slice((int) offset1, (int) (span.Length - offset1))).ToArray();
//             testStruct.E = Nethermind.Ssz.Ssz.DecodeBytes(span.Slice(10, 1)).ToArray();
//
//             BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), 11);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(4, 1), testStruct.B);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(5, 1), testStruct.C);
//             BinaryPrimitives.WriteInt32LittleEndian(span.Slice(6, 4), 12);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(10, 1), testStruct.E);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(11, 1), testStruct.A);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(12, 1), testStruct.D);
//             return testStruct;
//         }
//
//         private static void Encode(Span<byte> span, SmallTestStruct testStruct)
//         {
//             Nethermind.Ssz.Ssz.Encode(span.Slice(0, 2), testStruct.A);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(2, 2), testStruct.B);
//         }
//
//         private static SmallTestStruct DecodeSmallTestStruct(Span<byte> span)
//         {
//             SmallTestStruct testStruct = new SmallTestStruct();
//             testStruct.A = Nethermind.Ssz.Ssz.DecodeUShort(span.Slice(0, 2));
//             testStruct.B = Nethermind.Ssz.Ssz.DecodeUShort(span.Slice(2, 2));
//             return testStruct;
//         }
//
//         private static void Encode(Span<byte> span, SingleFieldTestStruct testStruct)
//         {
//             Nethermind.Ssz.Ssz.Encode(span, testStruct.A);
//         }
//
//         private static SingleFieldTestStruct DecodeSingleFieldTestStruct(Span<byte> span)
//         {
//             SingleFieldTestStruct testStruct = new SingleFieldTestStruct();
//             testStruct.A = Nethermind.Ssz.Ssz.DecodeByte(span);
//             return testStruct;
//         }
//
//         private static void Encode(Span<byte> span, VarTestStruct testStruct)
//         {
//             Nethermind.Ssz.Ssz.Encode(span.Slice(0, 2), testStruct.A);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(2, 4), 7U);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(6, 1), testStruct.C);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(7, 2 * testStruct.B.Length), testStruct.B);
//         }
//
//         private static VarTestStruct DecodeVarTestStruct(Span<byte> span)
//         {
//             VarTestStruct testStruct = new VarTestStruct();
//             testStruct.A = Nethermind.Ssz.Ssz.DecodeUShort(span.Slice(0, 2));
//             testStruct.C = Nethermind.Ssz.Ssz.DecodeByte(span.Slice(6, 1));
//             testStruct.B = Nethermind.Ssz.Ssz.DecodeUShorts(span.Slice(7)).ToArray();
//             return testStruct;
//         }
//
//         private static void Encode(Span<byte> span, FixedTestStruct testStruct)
//         {
//             Nethermind.Ssz.Ssz.Encode(span.Slice(0, 1), testStruct.A);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(1, 8), testStruct.B);
//             Nethermind.Ssz.Ssz.Encode(span.Slice(9, 4), testStruct.C);
//         }
//
//         private static FixedTestStruct DecodeFixTestStruct(Span<byte> span)
//         {
//             FixedTestStruct testStruct = new FixedTestStruct();
//             testStruct.A = Nethermind.Ssz.Ssz.DecodeByte(span.Slice(0, 1));
//             testStruct.B = Nethermind.Ssz.Ssz.DecodeULong(span.Slice(1, 8));
//             testStruct.C = Nethermind.Ssz.Ssz.DecodeUInt(span.Slice(9, 4));
//             return testStruct;
//         }
//
//         private static void Encode(ref Span<byte> span, ComplexTestStruct testStruct)
//         {
//             int offset = 0;
//             int varOffset = 71;
//
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, 2), testStruct.A);
//             offset += 2; // 2
//
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, 4), varOffset); // B
//             varOffset += testStruct.B.Length * 2;
//
//             offset += 4; // 6
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, 1), testStruct.C);
//
//             offset += 1; // 7
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, 4), varOffset); // D
//             varOffset += testStruct.D.Length;
//
//             offset += 4; // 11
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, 4), varOffset); // E
//             varOffset += 7 + testStruct.E.B.Length * 2;
//
//             offset += 4; // 15
//             foreach (FixedTestStruct fixedTestStruct in testStruct.F)
//             {
//                 Encode(span.Slice(offset, 13), fixedTestStruct);
//                 offset += 13; // 28, 41, 54, 67
//             }
//
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, 4), varOffset); // G
//             offset += 4; // 71
//
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, testStruct.B.Length * 2), testStruct.B);
//             offset += testStruct.B.Length * 2; // 71 + 256
//
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, testStruct.D.Length), testStruct.D);
//             offset += testStruct.D.Length; // 71 + 256 + 256
//
//             Encode(span.Slice(offset, 7 + testStruct.E.B.Length * 2), testStruct.E);
//             offset += 7 + testStruct.E.B.Length * 2; // 2638
//
//             Nethermind.Ssz.Ssz.Encode(span.Slice(offset, 4), 4 * testStruct.G.Count);
//             offset += 4;
//
//             foreach (VarTestStruct varTestStruct in testStruct.G)
//             {
//                 if (varTestStruct != testStruct.G.Last())
//                 {
//                     Nethermind.Ssz.Ssz.Encode(span.Slice(offset, 4), 4 * testStruct.G.Count + 7 + varTestStruct.B.Length * 2); // G
//                     offset += 4;
//                 }
//             }
//
//             foreach (VarTestStruct varTestStruct in testStruct.G)
//             {
//                 Encode(span.Slice(offset, 7 + varTestStruct.B.Length * 2), varTestStruct);
//                 offset += 7 + varTestStruct.B.Length * 2;
//             }
//
//             span = span.Slice(0, offset);
//         }
//
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
//         private class BitsStruct
//         {
//             public byte[] A { get; set; }
//             public byte[] B { get; set; } // fixed
//             public byte[] C { get; set; } // fixed
//             public byte[] D { get; set; }
//             public byte[] E { get; set; } // fixed
//         }
//
//         private class SmallTestStruct
//         {
//             public ushort A { get; set; }
//             public ushort B { get; set; }
//         }
//
//         private class SingleFieldTestStruct
//         {
//             public byte A { get; set; }
//         }
//
//         private class VarTestStruct
//         {
//             public ushort A { get; set; }
//             public ushort[] B { get; set; }
//             public byte C { get; set; }
//         }
//
//         private class FixedTestStruct
//         {
//             public byte A { get; set; }
//             public ulong B { get; set; }
//             public uint C { get; set; }
//         }
//
//         private class ComplexTestStruct
//         {
//             public ushort A { get; set; }
//             public ushort[] B { get; set; }
//             public byte C { get; set; }
//             public byte[] D { get; set; }
//             public VarTestStruct E { get; set; }
//             public List<FixedTestStruct> F { get; set; }
//             public List<VarTestStruct> G { get; set; }
//         }
//     }
// }