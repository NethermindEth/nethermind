// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class RlpReaderMessageExtensionsTests
{
    [Test]
    public void Trailing_data_disposes_decoded_message([Values] bool trailingData)
    {
        IDisposable message = Substitute.For<IDisposable>();
        byte[] data = trailingData ? [0xc0, 0x00] : [0xc0];

        if (trailingData)
        {
            Assert.Throws<RlpReaderException>(() => CheckEnd(data, message));
        }
        else
        {
            Assert.That(CheckEnd(data, message), Is.SameAs(message));
        }

        message.Received(trailingData ? 1 : 0).Dispose();

        static IDisposable CheckEnd(byte[] data, IDisposable message)
        {
            RlpReader ctx = new(data);
            ctx.SkipItem();
            return ctx.CheckEnd(message);
        }
    }
}
