// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Resettables;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class ResettableTests
    {
        private static (int[] array, int capacity, int currentPosition) CreateAtCapacity(
            int positionMultiplier = 1)
        {
            int capacity = Resettable.StartCapacity;
            int currentPosition = Resettable.StartCapacity * positionMultiplier;
            int[] array = new int[Resettable.StartCapacity];
            return (array, capacity, currentPosition);
        }

        [Test]
        public void Can_resize_up()
        {
            (int[] array, int capacity, int currentPosition) = CreateAtCapacity();
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);

            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity * Resettable.ResetRatio));
        }

        [Test]
        public void Resets_on_position_reset()
        {
            (int[] array, int capacity, int currentPosition) = CreateAtCapacity();
            array[0] = 30;

            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition);

            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity));
            Assert.That(array[0], Is.EqualTo(0));
        }

        [Test]
        public void Can_resize_down()
        {
            (int[] array, int capacity, int currentPosition) = CreateAtCapacity();
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);

            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity * Resettable.ResetRatio));

            currentPosition -= 2;
            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition, Resettable.StartCapacity);

            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity));
        }

        [Test]
        public void Does_not_resize_when_capacity_was_in_use()
        {
            (int[] array, int capacity, int currentPosition) = CreateAtCapacity();
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);

            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity * Resettable.ResetRatio));

            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition, Resettable.StartCapacity);

            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity * Resettable.ResetRatio));
        }

        [Test]
        public void Delays_downsizing()
        {
            (int[] array, int capacity, int currentPosition) = CreateAtCapacity(positionMultiplier: Resettable.ResetRatio);

            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);
            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity * Resettable.ResetRatio * Resettable.ResetRatio));

            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition);
            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity * Resettable.ResetRatio * Resettable.ResetRatio));

            Resettable<int>.Reset(ref array, ref capacity, ref currentPosition);
            Assert.That(array.Length, Is.EqualTo(Resettable.StartCapacity * Resettable.ResetRatio));
        }

        [Test]
        public void Copies_values_on_resize_up()
        {
            (int[] array, int capacity, int currentPosition) = CreateAtCapacity();
            array[0] = 30;
            Resettable<int>.IncrementPosition(ref array, ref capacity, ref currentPosition);

            Assert.That(array[0], Is.EqualTo(30));
        }
    }
}
