// //  Copyright (c) 2018 Demerzel Solutions Limited
// //  This file is part of the Nethermind library.
// // 
// //  The Nethermind library is free software: you can redistribute it and/or modify
// //  it under the terms of the GNU Lesser General Public License as published by
// //  the Free Software Foundation, either version 3 of the License, or
// //  (at your option) any later version.
// // 
// //  The Nethermind library is distributed in the hope that it will be useful,
// //  but WITHOUT ANY WARRANTY; without even the implied warranty of
// //  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// //  GNU Lesser General Public License for more details.
// // 
// //  You should have received a copy of the GNU Lesser General Public License
// //  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// // 
//
// using System;
// using FluentAssertions;
// using Nethermind.Core.Test.Builders;
// using NUnit.Framework;
//
// namespace Nethermind.Baseline.Test
// {
//     [TestFixture]
//     public class Bytes32Tests
//     {
//         [Test]
//         public void Equal_byte_arrays_give_equal_bytes32()
//         {
//             Bytes32 bytes32A = Bytes32.Wrap(TestItem.KeccakA.Bytes);
//             Bytes32 bytes32B = Bytes32.Wrap((byte[])TestItem.KeccakA.Bytes.Clone());
//             bytes32A.Should().Be(bytes32B);
//         }
//         
//         [Test]
//         public void Same_byte_arrays_give_equal_bytes32()
//         {
//             Bytes32 bytes32A = Bytes32.Wrap(TestItem.KeccakA.Bytes);
//             Bytes32 bytes32B = Bytes32.Wrap(TestItem.KeccakA.Bytes);
//             bytes32A.Should().Be(bytes32B);
//         }
//         
//         [Test]
//         public void Some_is_not_null()
//         {
//             Bytes32 bytes32A = Bytes32.Wrap(TestItem.KeccakA.Bytes);
//             bytes32A.Equals(null).Should().BeFalse();
//             bytes32A.Equals((object)null).Should().BeFalse();
//         }
//         
//         [Test]
//         public void Some_is_not_zero()
//         {
//             Bytes32 bytes32A = Bytes32.Wrap(TestItem.KeccakA.Bytes);
//             bytes32A.Equals(Bytes32.Zero).Should().BeFalse();
//             bytes32A.Equals((object)Bytes32.Zero).Should().BeFalse();
//         }
//         
//         [Test]
//         public void Same_is_equal()
//         {
//             Bytes32 bytes32A = Bytes32.Wrap(TestItem.KeccakA.Bytes);
//             bytes32A.Equals(bytes32A).Should().BeTrue();
//         }
//         
//         [Test]
//         public void Different_type_is_not_equal()
//         {
//             Bytes32 bytes32A = Bytes32.Wrap(TestItem.KeccakA.Bytes);
//             bytes32A.Equals(TestItem.KeccakA.Bytes).Should().BeFalse();
//         }
//         
//         [Test]
//         public void Zero_is_always_same()
//         {
//             // ReSharper disable once ConditionIsAlwaysTrueOrFalse
//             // ReSharper disable once EqualExpressionComparison
//             ReferenceEquals(Bytes32.Zero, Bytes32.Zero).Should().BeTrue();
//         }
//         
//         [Test]
//         public void Get_hash_code_is_consistent()
//         {
//             var wrappedA = Bytes32.Wrap(TestItem.KeccakA.Bytes);
//             Bytes32.Zero.GetHashCode().Should().NotBe(wrappedA.GetHashCode());
//             wrappedA.GetHashCode().Should().Be(wrappedA.GetHashCode());
//         }
//         
//         [TestCase(0)]
//         [TestCase(1)]
//         [TestCase(31)]
//         [TestCase(33)]
//         public void Invalid_length_throws(int length)
//         {
//             Assert.Throws<ArgumentException>(() => Bytes32.Wrap(new byte[length]));
//         }
//     }
// }