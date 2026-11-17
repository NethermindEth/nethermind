// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
public class RecorderTests
{
    [Test]
    public void Rotates_at_4_MiB_and_counts_the_entry_that_opens_a_file()
    {
        List<string> paths = [];
        IFile file = Substitute.For<IFile>();
        file.Create(Arg.Any<string>()).Returns((FileSystemStream)null!);
        file.When(static f => f.AppendAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(callInfo => paths.Add(callInfo.ArgAt<string>(0)));
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);

        Recorder recorder = new("rpc.{counter}.txt", fileSystem, LimboLogs.Instance.GetClassLogger<RecorderTests>());
        string entry = new('x', 1024 * 1024);
        for (int i = 0; i < 9; i++) recorder.RecordRequest(entry);

        // Four entries fill the first file exactly; the fifth opens file 1, and the ninth opens file 2 only if the fifth was counted there.
        Assert.That(paths, Is.EqualTo(new[]
        {
            "rpc.0.txt", "rpc.0.txt", "rpc.0.txt", "rpc.0.txt",
            "rpc.1.txt", "rpc.1.txt", "rpc.1.txt", "rpc.1.txt",
            "rpc.2.txt"
        }));
    }
}
