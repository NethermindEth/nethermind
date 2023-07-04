// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
            test.TestedDbFactory.CreateDb(test.RocksDbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 0)));
            test.TestedDbFactory.CreateDb(test.RocksDbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 1)));
        }

        [Test]
        public void if_old_db_present_creates_no_index_db()
        {
            TestContext test = new();
            test.Directory.Exists.Returns(true);
            test.Directory.EnumerateFiles().Returns(new[] { Substitute.For<IFileInfo>() });
            test.TestedDbFactory.CreateDb(test.RocksDbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test)));
            test.TestedDbFactory.CreateDb(test.RocksDbSettings);
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
            test.TestedDbFactory.CreateDb(test.RocksDbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 10)));
            test.TestedDbFactory.CreateDb(test.RocksDbSettings);
            test.RocksDbFactory.Received().CreateDb(Arg.Is(MatchSettings(test, 11)));
        }

        private static Expression<Predicate<RocksDbSettings>> MatchSettings(TestContext test, int? index = null)
        {
            string dbName = test.RocksDbSettings.DbName + index;
            string combine = Combine(test.RocksDbSettings.DbPath, index);
            return r => r.DbName == dbName && r.DbPath == combine;
        }

        private static string Combine(object path1, object path2) => path2 is null ? path1.ToString() : Path.Combine(path1.ToString(), path2.ToString());

        private class TestContext
        {
            private FullPruningInnerDbFactory _testedDbFactory;

            public RocksDbSettings RocksDbSettings = new("name", "path");
            public string Path => "path";
            public IRocksDbFactory RocksDbFactory { get; } = Substitute.For<IRocksDbFactory>();
            public IFileSystem FileSystem { get; } = Substitute.For<IFileSystem>();
            public IDirectoryInfo Directory { get; } = Substitute.For<IDirectoryInfo>();

            public FullPruningInnerDbFactory TestedDbFactory => _testedDbFactory ??= new(RocksDbFactory, FileSystem, Path);

            public TestContext()
            {
                FileSystem.Path.Combine(Arg.Any<string>(), Arg.Any<string>()).Returns(c => Combine(c[0], c[1]));
                FileSystem.DirectoryInfo.New(Path).Returns(Directory);
                RocksDbFactory.GetFullDbPath(Arg.Any<RocksDbSettings>()).Returns(c => c.Arg<RocksDbSettings>().DbPath);
            }
        }
    }
}
