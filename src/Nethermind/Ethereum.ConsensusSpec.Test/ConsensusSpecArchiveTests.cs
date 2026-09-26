// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class ConsensusSpecArchiveTests
{
    /// <summary>Suites that pick vectors by position must see the same order on every filesystem, not NTFS's case-folded one.</summary>
    [Test]
    public void LeafDirs_returns_case_directories_in_ordinal_order()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("leafdirs");
        try
        {
            // NTFS enumerates by upper-cased name, which puts "a" before "B" and "ab" before "a_b"; ordinal order is the reverse.
            // Created in ordinal order, so a filesystem listing newest first is also caught; nine names make a chance match unlikely elsewhere.
            string[] names = ["B", "a", "a_b", "ab", "c", "nested", "x1", "x10", "x2"];
            foreach (string name in names)
            {
                Directory.CreateDirectory(Path.Combine(root.FullName, name));
                File.WriteAllText(Path.Combine(root.FullName, name, "meta.yaml"), "");
            }

            Directory.CreateDirectory(Path.Combine(root.FullName, "unmarked", "case"));
            File.WriteAllText(Path.Combine(root.FullName, "unmarked", "case", "meta.yaml"), "");

            string[] found = [.. ConsensusSpecArchive.LeafDirs(root.FullName, "meta.yaml").Select(dir => Path.GetRelativePath(root.FullName, dir))];

            Assert.That(found, Is.EqualTo(new[] { "B", "a", "a_b", "ab", "c", "nested", Path.Combine("unmarked", "case"), "x1", "x10", "x2" }));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
