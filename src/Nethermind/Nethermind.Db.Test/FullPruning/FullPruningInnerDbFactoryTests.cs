//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            test.Directory.EnumerateFiles().Returns(new[] {Substitute.For<IFileInfo>()});
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
            test.Directory.EnumerateDirectories().Returns(new[] {dir10, ignoredDir, dir11});
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
                FileSystem.DirectoryInfo.FromDirectoryName(Path).Returns(Directory);
                RocksDbFactory.GetFullDbPath(Arg.Any<RocksDbSettings>()).Returns(c => c.Arg<RocksDbSettings>().DbPath);
            }
        }
    }
}
