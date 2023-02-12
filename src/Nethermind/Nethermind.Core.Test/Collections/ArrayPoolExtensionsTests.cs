// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class ArrayPoolExtensionsTests
{
    [Test]
    public void CanRentAndReturnArrayOfExactSize()
    {
        for (int i = 0; i < 20000; i++)
        {
            byte[] theArray = ArrayPool<byte>.Shared.RentExact(i);
            theArray.Length.Should().Be(i);
            ArrayPool<byte>.Shared.ReturnExact(theArray);
        }
    }
}
