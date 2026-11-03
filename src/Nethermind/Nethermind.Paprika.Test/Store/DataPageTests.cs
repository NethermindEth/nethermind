// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Int256;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class DataPageTests : BasePageTests
{
    private const uint BatchId = 1;

    [DebuggerStepThrough]
    private static byte[] GetValue(int i) => new UInt256((uint)i).ToBigEndian();

    [Test]
    public void Spinning_through_same_keys_should_use_limited_number_of_pages()
    {
        TestBatchContext batch = NewBatch(BatchId);
        DataPage data = ((IBatchContext)batch).GetNewPage<DataPage>(out _);

        const int spins = 100;
        const int count = 1024;

        for (int spin = 0; spin < spins; spin++)
        {
            for (int i = 0; i < count; i++)
            {
                Keccak keccak = default;
                BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
                NibblePath path = NibblePath.FromKey(keccak);

                data = new DataPage(data.Set(path, GetValue(i), batch));
            }

            batch = batch.Next();
        }

        for (int j = 0; j < count; j++)
        {
            Keccak search = default;
            BinaryPrimitives.WriteInt32LittleEndian(search.BytesAsSpan, j);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(data.TryGet(batch, NibblePath.FromKey(search), out ReadOnlySpan<byte> result), Is.True, $"Failed to read {j}");
                Assert.That(result.ToArray(), Is.EqualTo(GetValue(j)), $"Failed to read value of {j}");
            }
        }

        Assert.That(batch.PageCount, Is.LessThan(500));
    }
}
