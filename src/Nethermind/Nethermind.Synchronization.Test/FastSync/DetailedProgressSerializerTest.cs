// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.Random;
using Nethermind.Synchronization.FastSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

[TestFixture]
public class DetailedProgressSerializerTest
{
    private readonly DetailedProgress _data = new DetailedProgress(1, null);

    [Test]
    public void SerializerMultiThreadFuzzTest()
    {
        Task.Run(ChangeData);
        for (int i = 0; i < 1000000; i++)
        {
            _data.Serialize();
        }
    }

    private void ChangeData()
    {
        Random rand = new Random();
        Random randBool = new Random();
        while (true)
        {
            randBool.NextBoolean();
            Interlocked.Exchange(ref _data.ConsumedNodesCount, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange( ref _data.ConsumedNodesCount, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.SavedStorageCount, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.SavedStateCount, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.SavedNodesCount, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.SavedAccounts, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.SavedCode, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.RequestedNodesCount, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.DbChecks, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.StateWasThere, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.StateWasNotThere, randBool.NextBoolean()? rand.NextInt64(): 0);
            Interlocked.Exchange( ref _data.DataSize, randBool.NextBoolean()? rand.NextInt64(): 0);
        }
    }
}
