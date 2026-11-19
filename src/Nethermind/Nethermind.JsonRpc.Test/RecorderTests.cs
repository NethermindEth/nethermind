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
    private const int MaxFileLength = 4 * 1024 * 1024;

    [Test]
    public void Rotates_at_4_MiB_and_counts_the_entry_that_opens_a_file()
    {
        Recorder recorder = CreateRecorder(out _, out List<string> paths);
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

    [Test]
    public void Oversized_first_entry_goes_into_the_open_file()
    {
        Recorder recorder = CreateRecorder(out List<string> created, out List<string> paths);
        recorder.RecordRequest(new string('x', MaxFileLength + 1));
        recorder.RecordRequest("y");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths, Is.EqualTo(new[] { "rpc.0.txt", "rpc.1.txt" }));
            Assert.That(created, Is.EqualTo(new[] { "rpc.0.txt", "rpc.1.txt" }));
        }
    }

    private static Recorder CreateRecorder(out List<string> created, out List<string> paths)
    {
        List<string> createdPaths = created = [];
        List<string> appendedPaths = paths = [];
        IFile file = Substitute.For<IFile>();
        file.Create(Arg.Any<string>()).Returns(callInfo =>
        {
            createdPaths.Add(callInfo.ArgAt<string>(0));
            return (FileSystemStream)null!;
        });
        file.When(static f => f.AppendAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(callInfo => appendedPaths.Add(callInfo.ArgAt<string>(0)));
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);

        return new Recorder("rpc.{counter}.txt", fileSystem, LimboLogs.Instance.GetClassLogger<RecorderTests>());
    }
}
