// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class BencodeTests
{
    [Test]
    public void Decode_rejects_excessive_nesting_depth()
    {
        StringBuilder builder = new();
        for (int i = 0; i < 130; i++)
        {
            builder.Append('l');
        }

        for (int i = 0; i < 130; i++)
        {
            builder.Append('e');
        }

        byte[] payload = Encoding.ASCII.GetBytes(builder.ToString());

        Assert.That(() => BencodeDocument.Decode(payload), Throws.TypeOf<FormatException>());
    }
}
