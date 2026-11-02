// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Test.IO;
using Nethermind.State.Flat.Io;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Io;

public class BlockBufferedFileTests
{
    /// <remarks>The ranges sit inside a block, straddle block boundaries, reach into the short last block, and
    /// alternate between far blocks, so reads come from both held blocks and from reloads.</remarks>
    [Test]
    public void Reads_match_the_file_across_blocks_and_refuse_ranges_past_its_end()
    {
        byte[] content = new byte[2 * 4096 + 100];
        for (int index = 0; index < content.Length; index++) content[index] = (byte)(index % 251);
        using TempPath path = TempPath.GetTempFile();
        File.WriteAllBytes(path.Path, content);
        using BlockBufferedFile file = new(path.Path);

        (int Offset, int Length)[] ranges = [(0, 16), (4090, 12), (8192, 100), (100, 8), (8190, 102), (100, 8), (0, content.Length)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(file.Length, Is.EqualTo(content.Length));
            foreach ((int offset, int length) in ranges)
            {
                byte[] read = new byte[length];
                Assert.That(file.TryRead(offset, read), Is.True);
                Assert.That(read, Is.EqualTo(content[offset..(offset + length)]), $"{length} bytes at {offset}");
            }
            using (NoOpPin pin = file.PinBuffer(new Bound(4000, 200)))
                Assert.That(pin.Buffer.ToArray(), Is.EqualTo(content[4000..4200]));
            Assert.That(file.TryRead(content.Length - 1, new byte[2]), Is.False);
            Assert.That(file.TryRead(-1, new byte[1]), Is.False);
        }
    }
}
