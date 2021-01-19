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

using Nethermind.Core.Resettables;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class ResettableTests
    {
        [Test]
        public void Can_resize_up()
        {
            int capacity = Resettable.StartCapacity;
            int currentPosition = Resettable.StartCapacity;
            int[] array = new int[Resettable.StartCapacity];
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);
            
            Assert.AreEqual(Resettable.StartCapacity * Resettable.ResetRatio, array.Length);
        }

        [Test]
        public void Resets_on_position_reset()
        {
            int capacity = Resettable.StartCapacity;
            int currentPosition = Resettable.StartCapacity;
            int[] array = new int[Resettable.StartCapacity];
            array[0] = 30;
            
            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition);
            
            Assert.AreEqual(Resettable.StartCapacity, array.Length);
            Assert.AreEqual(0, array[0]);
        }
        
        [Test]
        public void Can_resize_down()
        {
            int capacity = Resettable.StartCapacity;
            int currentPosition = capacity;
            int[] array = new int[Resettable.StartCapacity];
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);
            
            Assert.AreEqual(Resettable.StartCapacity * Resettable.ResetRatio, array.Length);

            currentPosition -= 2;
            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition, Resettable.StartCapacity);
            
            Assert.AreEqual(Resettable.StartCapacity, array.Length);
        }
        
        [Test]
        public void Does_not_resize_when_capacity_was_in_use()
        {
            int capacity = Resettable.StartCapacity;
            int currentPosition = Resettable.StartCapacity;
            int[] array = new int[Resettable.StartCapacity];
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);
            
            Assert.AreEqual(Resettable.StartCapacity * Resettable.ResetRatio, array.Length);
            
            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition, Resettable.StartCapacity);
            
            Assert.AreEqual(Resettable.StartCapacity * Resettable.ResetRatio, array.Length);
        }
        
        [Test]
        public void Delays_downsizing()
        {
            int capacity = Resettable.StartCapacity;
            int currentPosition = Resettable.StartCapacity * Resettable.ResetRatio;
            int[] array = new int[Resettable.StartCapacity];
            
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);
            Assert.AreEqual(Resettable.StartCapacity * Resettable.ResetRatio * Resettable.ResetRatio, array.Length);
            
            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition);
            Assert.AreEqual(Resettable.StartCapacity * Resettable.ResetRatio * Resettable.ResetRatio, array.Length);
            
            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition);
            Assert.AreEqual(Resettable.StartCapacity * Resettable.ResetRatio, array.Length);
        }
        
        [Test]
        public void Copies_values_on_resize_up()
        {
            int capacity = Resettable.StartCapacity;
            int currentPosition = capacity;
            int[] array = new int[Resettable.StartCapacity];
            array[0] = 30;
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);
            
            Assert.AreEqual(30, array[0]);
        }
    }
}
