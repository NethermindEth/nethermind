// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq.Expressions;
using Nethermind.Core.Test.IO;
using Nethermind.Db.FullPruning;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Db.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class FullPruningInnerDbFactoryTests
    {
        [Test]
        public void if_no_db_present_creates_0_index_db()
        {
            TestContext test = new();
            test.Directory.Exists.Returns(false);
            test.TestedDbFactory.CreateDb(test.DbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 0)));
            test.TestedDbFactory.CreateDb(test.DbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 1)));
        }

        [Test]
        public void if_old_db_present_creates_no_index_db()
        {
            TestContext test = new();
            test.Directory.Exists.Returns(true);
            test.Directory.EnumerateFiles().Returns(new[] { Substitute.For<IFileInfo>() });
            test.TestedDbFactory.CreateDb(test.DbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test)));
            test.TestedDbFactory.CreateDb(test.DbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 0)));
        }

        [Test]
        public void if_new_db_present_creates_next_index_db()
        {
            TestContext test = new();
            test.Directory.Exists.Returns(true);
            IDirectoryInfo dir10 = Substitute.For<IDirectoryInfo>();
            dir10.Name.Returns(10.ToString());
            IDirectoryInfo dir11 = Substitute.For<IDirectoryInfo>();
            dir11.Name.Returns(11.ToString());
            IDirectoryInfo ignoredDir = Substitute.For<IDirectoryInfo>();
            test.Directory.EnumerateDirectories().Returns(new[] { dir10, ignoredDir, dir11 });
            test.TestedDbFactory.CreateDb(test.DbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 10)));
            test.TestedDbFactory.CreateDb(test.DbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 11)));
        }

        [Test]
        public void deletes_indexed_dbs_other_than_the_one_it_opens_next()
        {
            TestContext test = new();
            test.Directory.Exists.Returns(true);
            IDirectoryInfo current = Dir("3");
            IDirectoryInfo leftover = Dir("4");
            IDirectoryInfo unrelated = Dir("tmp");
            test.Directory.EnumerateDirectories().Returns(new[] { leftover, current, unrelated });

            int deleted = test.TestedDbFactory.DeleteStaleInnerDbs();

            Assert.That(deleted, Is.EqualTo(1));
            leftover.Received(1).Delete(true);
            current.DidNotReceive().Delete(Arg.Any<bool>());
            unrelated.DidNotReceive().Delete(Arg.Any<bool>());
            test.TestedDbFactory.CreateDb(test.DbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 3)));
        }

        [Test]
        public void keeps_the_live_db_when_the_factory_already_resolved_a_path()
        {
            // The live DB comes from disk, so what the factory was asked before does not change what is deleted.
            TestContext test = new();
            test.Directory.Exists.Returns(true);
            IDirectoryInfo live = Dir("0");
            IDirectoryInfo leftover = Dir("1");
            test.Directory.EnumerateDirectories().Returns(new[] { live, leftover });
            test.TestedDbFactory.GetFullDbPath(test.DbSettings);

            Assert.That(test.TestedDbFactory.DeleteStaleInnerDbs(), Is.EqualTo(1));
            live.DidNotReceive().Delete(Arg.Any<bool>());
            leftover.Received(1).Delete(true);
        }

        public enum Layout { NoDb, LegacyDb, IndexedDb }

        [TestCase(Layout.NoDb, 0, 1)]
        [TestCase(Layout.LegacyDb, null, 0)]
        [TestCase(Layout.IndexedDb, 10, 11)]
        public void GetFullDbPath_returns_the_next_db_path_without_advancing(Layout layout, int? next, int? afterNext)
        {
            TestContext test = new();
            IDirectoryInfo indexedDb = Dir("10");
            test.Directory.Exists.Returns(layout != Layout.NoDb);
            if (layout == Layout.LegacyDb) test.Directory.EnumerateFiles().Returns(new[] { Substitute.For<IFileInfo>() });
            if (layout == Layout.IndexedDb) test.Directory.EnumerateDirectories().Returns(new[] { indexedDb });

            Assert.That(test.TestedDbFactory.GetFullDbPath(test.DbSettings), Is.EqualTo(Combine(test.Path, next)));
            Assert.That(test.TestedDbFactory.GetFullDbPath(test.DbSettings), Is.EqualTo(Combine(test.Path, next)));
            test.TestedDbFactory.CreateDb(test.DbSettings);
            test.RocksDbFactory.Received(1).CreateDb(Arg.Is(MatchSettings(test, next)));
            Assert.That(test.TestedDbFactory.GetFullDbPath(test.DbSettings), Is.EqualTo(Combine(test.Path, afterNext)));
        }

        [Test]
        public void deletes_every_indexed_db_when_the_main_directory_holds_the_db()
        {
            TestContext test = new();
            test.Directory.Exists.Returns(true);
            test.Directory.EnumerateFiles().Returns(new[] { Substitute.For<IFileInfo>() });
            IDirectoryInfo leftover = Dir("0");
            test.Directory.EnumerateDirectories().Returns(new[] { leftover });

            Assert.That(test.TestedDbFactory.DeleteStaleInnerDbs(), Is.EqualTo(1));
            leftover.Received(1).Delete(true);
        }

        [Test]
        public void deletes_nothing_when_no_db_present()
        {
            TestContext test = new();
            test.Directory.Exists.Returns(false);
            // What a real file system does when a missing directory is enumerated.
            test.Directory.EnumerateDirectories().Returns(_ => throw new DirectoryNotFoundException());

            Assert.That(test.TestedDbFactory.DeleteStaleInnerDbs(), Is.EqualTo(0));
        }

        [Test]
        public void opens_the_newer_indexed_db_when_deleting_the_older_one_did_not_finish()
        {
            using TempPath tempPath = TempPath.GetTempDirectory();
            string older = Path.Combine(tempPath.Path, "0");
            string newer = Path.Combine(tempPath.Path, "1");
            Directory.CreateDirectory(older);
            Directory.CreateDirectory(newer);
            File.WriteAllText(Path.Combine(older, "000001.sst"), "data");
            File.WriteAllText(Path.Combine(older, DbSettings.DeletingMarkerFileName), "marker");
            File.WriteAllText(Path.Combine(newer, "000002.sst"), "data");
            IDbFactory dbFactory = CreatePassThroughDbFactory();

            new FullPruningInnerDbFactory(dbFactory, new RealFileSystem(), tempPath.Path).CreateDb(new DbSettings("name", tempPath.Path));

            dbFactory.Received(1).CreateDb(Arg.Is<DbSettings>(s => s.DbPath == newer));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.Exists(older), Is.False);
                Assert.That(File.Exists(Path.Combine(newer, "000002.sst")), Is.True);
            }
        }

        [Test]
        public void opens_the_indexed_db_when_deleting_the_main_one_did_not_finish()
        {
            using TempPath tempPath = TempPath.GetTempDirectory();
            string indexed = Path.Combine(tempPath.Path, "0");
            Directory.CreateDirectory(indexed);
            File.WriteAllText(Path.Combine(tempPath.Path, "000001.sst"), "data");
            File.WriteAllText(Path.Combine(tempPath.Path, DbSettings.DeletingMarkerFileName), "marker");
            File.WriteAllText(Path.Combine(indexed, "000002.sst"), "data");
            IDbFactory dbFactory = CreatePassThroughDbFactory();

            new FullPruningInnerDbFactory(dbFactory, new RealFileSystem(), tempPath.Path).CreateDb(new DbSettings("name", tempPath.Path));

            dbFactory.Received(1).CreateDb(Arg.Is<DbSettings>(s => s.DbPath == indexed));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.EnumerateFiles(tempPath.Path), Is.Empty);
                Assert.That(File.Exists(Path.Combine(indexed, "000002.sst")), Is.True);
            }
        }

        private static IDbFactory CreatePassThroughDbFactory()
        {
            IDbFactory dbFactory = Substitute.For<IDbFactory>();
            dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(static c => c.Arg<DbSettings>().DbPath);
            return dbFactory;
        }

        private static IDirectoryInfo Dir(string name)
        {
            IDirectoryInfo directory = Substitute.For<IDirectoryInfo>();
            directory.Name.Returns(name);
            return directory;
        }

        private static Expression<Predicate<DbSettings>> MatchSettings(TestContext test, int? index = null)
        {
            string dbName = test.DbSettings.DbName + index;
            string combine = Combine(test.DbSettings.DbPath, index);
            return r => r.DbName == dbName && r.DbPath == combine;
        }

        private static string Combine(object path1, object path2) => path2 is null ? path1.ToString() : Path.Combine(path1.ToString(), path2.ToString());

        private class TestContext
        {
            private FullPruningInnerDbFactory _testedDbFactory;

            public DbSettings DbSettings = new("name", "path");
            public string Path => "path";
            public IDbFactory RocksDbFactory { get; } = Substitute.For<IDbFactory>();
            public IFileSystem FileSystem { get; } = Substitute.For<IFileSystem>();
            public IDirectoryInfo Directory { get; } = Substitute.For<IDirectoryInfo>();

            public FullPruningInnerDbFactory TestedDbFactory => _testedDbFactory ??= new(RocksDbFactory, FileSystem, Path);

            public TestContext()
            {
                FileSystem.Path.Combine(Arg.Any<string>(), Arg.Any<string>()).Returns(static c => Combine(c[0], c[1]));
                FileSystem.DirectoryInfo.New(Path).Returns(Directory);
                RocksDbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(static c => c.Arg<DbSettings>().DbPath);
            }
        }
    }
}
