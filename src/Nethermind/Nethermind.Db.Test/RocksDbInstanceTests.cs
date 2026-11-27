// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.All)]
    public class RocksDbInstanceTests
    {
        private const string DbPath = "db";
        private static readonly string Marker = Path.Join(DbPath, DbSettings.DeletingMarkerFileName);
        private static readonly string Current = Path.Join(DbPath, "CURRENT");
        private static readonly string Table = Path.Join(DbPath, "000001.sst");

        [Test]
        public void Warmup_reads_to_the_end_after_a_short_read()
        {
            using ShortReadStream stream = new(new byte[7]);
            Assert.That(RocksDbFactory.ReadForWarmup(stream, new byte[4]), Is.EqualTo(stream.Length));
        }

        private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(2, buffer.Length)]);
        }

        [Test]
        public void Delete_keeps_the_marker_when_a_file_cannot_be_deleted([Values] bool canDeleteFolder)
        {
            IFileSystem fileSystem = CreateFileSystem(out List<string> calls, Table);

            bool deleted = RocksDbInstance.Delete(DbPath, new DbSettings("name", DbPath) { CanDeleteFolder = canDeleteFolder }, fileSystem, LimboLogs.Instance.GetClassLogger<RocksDbInstanceTests>());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(deleted, Is.False);
                Assert.That(calls, Is.EqualTo(new[] { $"write {Marker}", $"delete {Current}", $"delete {Table}" }));
            }
        }

        [Test]
        public void Delete_removes_the_marker_after_every_file([Values] bool canDeleteFolder)
        {
            string lastCall = canDeleteFolder ? $"delete folder {DbPath}" : $"delete {Marker}";
            IFileSystem fileSystem = CreateFileSystem(out List<string> calls, null);

            bool deleted = RocksDbInstance.Delete(DbPath, new DbSettings("name", DbPath) { CanDeleteFolder = canDeleteFolder }, fileSystem, LimboLogs.Instance.GetClassLogger<RocksDbInstanceTests>());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(deleted, Is.True);
                Assert.That(calls, Is.EqualTo(new[] { $"write {Marker}", $"delete {Current}", $"delete {Table}", lastCall }));
            }
        }

        [Test]
        public void Delete_removes_every_file_when_the_marker_cannot_be_written([Values] bool canDeleteFolder)
        {
            string lastCall = canDeleteFolder ? $"delete folder {DbPath}" : $"delete {Marker}";
            IFileSystem fileSystem = CreateFileSystem(out List<string> calls, null, failMarker: true);

            bool deleted = RocksDbInstance.Delete(DbPath, new DbSettings("name", DbPath) { CanDeleteFolder = canDeleteFolder }, fileSystem, LimboLogs.Instance.GetClassLogger<RocksDbInstanceTests>());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(deleted, Is.True);
                Assert.That(calls, Is.EqualTo(new[] { $"delete {Current}", $"delete {Table}", lastCall }));
            }
        }

        private static IFileSystem CreateFileSystem(out List<string> calls, string? failingFile, bool failMarker = false)
        {
            List<string> received = [];
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            fileSystem.Directory.Exists(DbPath).Returns(true);
            fileSystem.Directory.EnumerateFiles(DbPath).Returns(_ => received.Contains($"write {Marker}") ? new[] { Current, Marker, Table } : new[] { Current, Table });
            fileSystem.File.When(static f => f.WriteAllBytes(Arg.Any<string>(), Arg.Any<byte[]>())).Do(c =>
            {
                if (failMarker) throw new IOException();
                received.Add($"write {c.ArgAt<string>(0)}");
            });
            fileSystem.File.When(static f => f.Delete(Arg.Any<string>())).Do(c =>
            {
                received.Add($"delete {c.ArgAt<string>(0)}");
                if (c.ArgAt<string>(0) == failingFile) throw new IOException();
            });
            fileSystem.Directory.When(static d => d.Delete(Arg.Any<string>(), true)).Do(c => received.Add($"delete folder {c.ArgAt<string>(0)}"));
            calls = received;
            return fileSystem;
        }
    }
}
