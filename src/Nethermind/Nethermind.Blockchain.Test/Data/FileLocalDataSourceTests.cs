//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.Data;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Data
{
    public class FileLocalDataSourceTests
    {
        [Test]
        public void correctly_reads_existing_file()
        {
            using var tempFile = TempPath.GetTempFile();
            File.WriteAllText(tempFile.Path, GenerateStringJson("A", "B", "C"));
            // var x = new EthereumJsonSerializer().Serialize(new string []{"A", "B", "C"});
            var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), LimboLogs.Instance);
            fileLocalDataSource.Data.Should().BeEquivalentTo("A", "B", "C");
        }
        
        [Test]
        public void correctly_updates_from_existing_file()
        {
            using var tempFile = TempPath.GetTempFile();
            File.WriteAllText(tempFile.Path, GenerateStringJson("A"));
            var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), LimboLogs.Instance);
            bool changedRaised = false;
            var handle = new ManualResetEventSlim(false);
            fileLocalDataSource.Changed += (sender, args) =>
            {
                changedRaised = true;
                handle.Set();
            }; 
            File.WriteAllText(tempFile.Path, GenerateStringJson("C", "B"));
            handle.Wait(TimeSpan.FromMilliseconds(100));
            changedRaised.Should().BeTrue();
            fileLocalDataSource.Data.Should().BeEquivalentTo("C", "B");
        }

        [Test]
        public void correctly_updates_from_new_file()
        {
            using var tempFile = TempPath.GetTempFile();
            var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), LimboLogs.Instance);
            bool changedRaised = false;
            var handle = new ManualResetEventSlim(false);
            fileLocalDataSource.Changed += (sender, args) =>
            {
                changedRaised = true;
                handle.Set();
            };  
            File.WriteAllText(tempFile.Path, GenerateStringJson("A", "B"));
            handle.Wait(TimeSpan.FromMilliseconds(100));
            fileLocalDataSource.Data.Should().BeEquivalentTo("A", "B");
            changedRaised.Should().BeTrue();
        }

        private static string GenerateStringJson(params string[] items) => $"[{string.Join(", ", items.Select(i => $"\"{i}\""))}]";
    }
}
