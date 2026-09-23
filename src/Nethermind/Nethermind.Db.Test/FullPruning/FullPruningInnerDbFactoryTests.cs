// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq.Expressions;
using Nethermind.Db.FullPruning;
using NSubstitute;
using NUnit.Framework;

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
            // GetFullDbPath advances the factory's index as a side effect, so the live DB must come from disk.
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

            Assert.That(test.TestedDbFactory.DeleteStaleInnerDbs(), Is.EqualTo(0));
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
