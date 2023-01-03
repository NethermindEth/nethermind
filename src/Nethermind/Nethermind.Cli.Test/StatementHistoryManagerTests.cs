// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using Nethermind.Cli.Console;
using NSubstitute;

namespace Nethermind.Cli.Test;

using NUnit.Framework;

public class StatementHistoryManagerTests
{
    private ICliConsole _console;
    private IFileSystem _fileSystem;
    private IFile _file;
    private StatementHistoryManager _historyManager;

    [SetUp]
    public void SetUp()
    {
        _console = Substitute.For<ICliConsole>();
        _fileSystem = Substitute.For<IFileSystem>();
        _file = Substitute.For<IFile>();
        _fileSystem.File.Returns(_file);
        _historyManager = new(_console, _fileSystem);
        ReadLine.ClearHistory();
    }


    [Test]
    public void should_write_removed_to_history_if_secured_command_received()
    {
        List<string> fileContents = new();
        _file.Exists(Arg.Any<string>()).Returns(true);
        _file.AppendAllLines(Arg.Any<string>(), Arg.Do<string[]>(x => fileContents.AddRange(x)));

        _historyManager.UpdateHistory("notSecured");

        CollectionAssert.AreEqual(new[] { "notSecured" }, fileContents);
        CollectionAssert.AreEqual(new[] { "notSecured" }, ReadLine.GetHistory());

        _historyManager.UpdateHistory("unlockAccount");

        CollectionAssert.AreEqual(new[] { "notSecured" }, fileContents);
        CollectionAssert.AreEqual(new[] { "notSecured", "*removed*" }, ReadLine.GetHistory());

        _historyManager.UpdateHistory("notSecured2");

        CollectionAssert.AreEqual(new[] { "notSecured", "notSecured2" }, fileContents);
        CollectionAssert.AreEqual(new[] { "notSecured", "*removed*", "notSecured2" }, ReadLine.GetHistory());

        _historyManager.UpdateHistory("newAccount");

        CollectionAssert.AreEqual(new[] { "notSecured", "notSecured2" }, fileContents);
        CollectionAssert.AreEqual(new[] { "notSecured", "*removed*", "notSecured2", "*removed*" }, ReadLine.GetHistory());
    }

    [Test]
    public void Init_should_read_history_from_file()
    {
        _file.Exists(Arg.Any<string>()).Returns(true);

        List<string> fileContents = new() { "ab", "cd", "efg" };
        _file.ReadLines(Arg.Any<string>()).Returns(fileContents);

        _historyManager.Init();

        CollectionAssert.AreEqual(fileContents, ReadLine.GetHistory());
    }
}
