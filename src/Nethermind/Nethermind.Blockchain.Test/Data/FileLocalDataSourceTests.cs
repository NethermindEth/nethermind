// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        [Test, Timeout(Timeout.MaxTestTime)]
        public void correctly_reads_existing_file()
        {
            using (var tempFile = TempPath.GetTempFile())
            {
                File.WriteAllText(tempFile.Path, GenerateStringJson("A", "B", "C"));
                // var x = new EthereumJsonSerializer().Serialize(new string []{"A", "B", "C"});
                using var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), new FileSystem(), LimboLogs.Instance);
                fileLocalDataSource.Data.Should().BeEquivalentTo("A", "B", "C");
            }
        }

        [Ignore("flaky")]
        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task correctly_updates_from_existing_file()
        {
            using (var tempFile = TempPath.GetTempFile())
            {
                await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A"));
                int interval = 30;
                using (var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), new FileSystem(), LimboLogs.Instance, interval))
                {
                    int changedRaised = 0;
                    var handle = new SemaphoreSlim(0);
                    fileLocalDataSource.Changed += (sender, args) =>
                    {
                        changedRaised++;
                        handle.Release();
                    };
                    await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("C", "B"));
                    await handle.WaitAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime));
                    changedRaised.Should().Be(1);
                    fileLocalDataSource.Data.Should().BeEquivalentTo("C", "B");

                    await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("E", "F"));
                    await handle.WaitAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime));
                    changedRaised.Should().Be(2);
                    fileLocalDataSource.Data.Should().BeEquivalentTo("E", "F");
                }
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        [Ignore("flaky test")]
        public async Task correctly_updates_from_new_file()
        {
            using (var tempFile = TempPath.GetTempFile())
            using (var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), new FileSystem(), LimboLogs.Instance, 10))
            {
                bool changedRaised = false;
                var handle = new SemaphoreSlim(0);
                fileLocalDataSource.Changed += (sender, args) =>
                {
                    changedRaised = true;
                    handle.Release();
                };
                await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A", "B"));
                await handle.WaitAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime));
                fileLocalDataSource.Data.Should().BeEquivalentTo("A", "B");
                changedRaised.Should().BeTrue();
            }
        }

        private static string GenerateStringJson(params string[] items) => $"[{string.Join(", ", items.Select(i => $"\"{i}\""))}]";

        [Test, Timeout(Timeout.MaxTestTime)]
        public void loads_default_when_failed_loading_file()
        {
            using var tempFile = TempPath.GetTempFile();
            using (File.Open(tempFile.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                using var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), new FileSystem(), LimboLogs.Instance);
                fileLocalDataSource.Data.Should().BeEquivalentTo(default);
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        [Retry(10)]
        [Ignore("Causing repeated pains on GitHub actions.")]
        public async Task retries_loading_file()
        {
            using (var tempFile = TempPath.GetTempFile())
            {
                await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A", "B", "C"));
                int interval = 30;
                using var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), new FileSystem(), LimboLogs.Instance, interval);
                using (var file = File.Open(tempFile.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var writer = new StreamWriter(file, leaveOpen: true))
                    {
                        await writer.WriteAsync(GenerateStringJson("A", "B", "C", "D"));
                    }

                    await Task.Delay(10 * interval);

                    fileLocalDataSource.Data.Should().BeEquivalentTo("A", "B", "C");
                }

                await Task.Delay(10 * interval);

                fileLocalDataSource.Data.Should().BeEquivalentTo("A", "B", "C", "D");
            }
        }

        [Ignore("flaky test")]
        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task loads_default_when_deleted_file()
        {
            using (var tempFile = TempPath.GetTempFile())
            {
                await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("A"));
                using (var fileLocalDataSource = new FileLocalDataSource<string[]>(tempFile.Path, new EthereumJsonSerializer(), new FileSystem(), LimboLogs.Instance, 50))
                {
                    int changedRaised = 0;
                    var handle = new SemaphoreSlim(0);
                    fileLocalDataSource.Changed += (sender, args) =>
                    {
                        changedRaised++;
                        handle.Release();
                    };
                    await File.WriteAllTextAsync(tempFile.Path, GenerateStringJson("C", "B"));
                    await handle.WaitAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime));
                    changedRaised.Should().Be(1);

                    fileLocalDataSource.Data.Should().BeEquivalentTo("C", "B");

                    File.Delete(tempFile.Path);
                    await handle.WaitAsync(TimeSpan.FromMilliseconds(Timeout.MaxWaitTime));
                    changedRaised.Should().Be(2);
                    fileLocalDataSource.Data.Should().BeNull();
                }
            }
        }
    }
}
