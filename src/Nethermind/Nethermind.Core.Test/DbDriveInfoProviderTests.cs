// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Core.Test;

public class DbDriveInfoProviderTests
{
    private static readonly string Root = Path.GetPathRoot(Path.GetTempPath())!;
    private static readonly string Main = Path.Combine(Root, "main");
    private static readonly string Other = Path.Combine(Root, "other");
    private static readonly string Db = Path.Combine(Main, "db");

    [TestCase("/data10/state", "/|/data1", ExpectedResult = "/", ExcludePlatform = "Win")]
    [TestCase("/data1/state", "/|/data1", ExpectedResult = "/data1", ExcludePlatform = "Win")]
    [TestCase("/data1", "/data1|/", ExpectedResult = "/data1", ExcludePlatform = "Win")]
    [TestCase("/data1/nested/db", "/data1/nested|/|/data1", ExpectedResult = "/data1/nested", ExcludePlatform = "Win")]
    [TestCase("/data1/state", "/|/data1/", ExpectedResult = "/data1/", ExcludePlatform = "Win")]
    [TestCase("/Data1/state", "/|/data1", ExpectedResult = "/", ExcludePlatform = "Win")]
    [TestCase("/data10", "/data1", ExpectedResult = null, ExcludePlatform = "Win")]
    [TestCase(@"c:\db", @"C:\", ExpectedResult = @"C:\", IncludePlatform = "Win")]
    [TestCase(@"D:\data10\state", @"C:\|D:\|D:\data1", ExpectedResult = @"D:\", IncludePlatform = "Win")]
    [TestCase(@"D:\data1\state", @"D:\|D:\data1", ExpectedResult = @"D:\data1", IncludePlatform = "Win")]
    public string? FindDrive_matches_roots_on_whole_path_components(string path, string roots) =>
        DbDriveInfoProvider.FindDrive([.. roots.Split('|').Select(Drive)], path)?.RootDirectory.FullName;

    [Test]
    [NonParallelizable] // swaps the static log manager
    [Platform(Exclude = "MacOsX")] // GetDriveInfos lists no drives on macOS
    public void Lists_each_drive_once_with_the_top_level_drive_first()
    {
        IDriveInfo main = Drive(Main), other = Drive(Other);
        string broken = Path.Combine(Db, "broken");
        IDirectoryInfo brokenLink = Dir(broken, broken);
        brokenLink.ResolveLinkTarget(true).Throws(new IOException("Too many levels of symbolic links"));
        IDirectoryInfo[] children =
        [
            Dir(Path.Combine(Db, "plain")),
            Dir(Path.Combine(Db, "a"), Path.Combine(Other, "a"), Path.Combine(Other, "a")),
            Dir(Path.Combine(Db, "b"), Path.Combine(Other, "b"), Path.Combine(Other, "b")),
            Dir(Path.Combine(Db, "back"), Path.Combine(Main, "back"), Path.Combine(Main, "back")),
            brokenLink,
        ];
        IFileSystem fileSystem = FileSystem(Dir(Db), children, other, Drive(Root), main);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();

        Assert.That(GetDriveInfosLoggingTo(logger, fileSystem), Is.EqualTo(new[] { main, other }));

        logger.Received(1).Warn(Arg.Is<string>(text => text.Contains(broken)));
        fileSystem.DirectoryInfo.DidNotReceive().New(Root);
    }

    [Test]
    [NonParallelizable] // swaps the static log manager
    [Platform(Include = "Linux")] // Windows resolves only a linked directory itself, and macOS lists no drives
    public void Keeps_the_database_drive_when_a_link_under_a_linked_parent_cannot_be_resolved()
    {
        IDriveInfo main = Drive(Main), other = Drive(Other);
        string removed = Path.Combine(Db, "removed"), physical = Path.Combine(Other, "db", "removed");
        IFileSystem fileSystem = FileSystem(Dir(Db), [Dir(removed, Path.Combine(Other, "state"))], main, other);
        IDirectoryInfo linkedMain = Dir(Main, Other);
        fileSystem.DirectoryInfo.New(Main).Returns(linkedMain);
        // Listed as a link, then removed before it is resolved
        IDirectoryInfo removedLater = Dir(physical, exists: false);
        fileSystem.DirectoryInfo.New(physical).Returns(removedLater);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();

        Assert.That(GetDriveInfosLoggingTo(logger, fileSystem), Is.EqualTo(new[] { other }));
        logger.Received(1).Warn(Arg.Any<string>());
        logger.Received(1).Warn(Arg.Is<string>(text => text.Contains(removed)));
    }

    [TestCase("absolute")]
    [TestCase("relative")]
    [TestCase("chain")]
    [TestCase("linked db directory")]
    [TestCase("linked db directory, trailing separator")]
    [TestCase("linked db directory, '..' after a link in its target")]
    [TestCase("linked parent directory")]
    [TestCase("dangling and looping")]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows, and macOS lists no drives
    public void Resolves_real_links_to_their_final_target(string link)
    {
        using TempPath temp = TempPath.GetTempDirectory();
        string main = Path.Combine(temp.Path, "main"), other = Path.Combine(temp.Path, "other");
        string db = Path.Combine(main, "db"), state = Path.Combine(db, "state"), target = Path.Combine(other, "state");
        bool linkedDb = link.StartsWith("linked db directory", StringComparison.Ordinal);
        bool linkedParent = link == "linked parent directory";
        Directory.CreateDirectory(target);
        if (!linkedParent)
        {
            Directory.CreateDirectory(linkedDb ? main : db);
        }
        switch (link)
        {
            case "absolute":
                Directory.CreateSymbolicLink(state, target);
                break;
            case "relative":
                Directory.CreateSymbolicLink(state, Path.Combine("..", "..", "other", "state"));
                break;
            case "chain":
                Directory.CreateSymbolicLink(Path.Combine(main, "hop"), target);
                Directory.CreateSymbolicLink(state, Path.Combine(main, "hop"));
                break;
            case "linked db directory" or "linked db directory, trailing separator":
                Directory.CreateSymbolicLink(db, Path.Combine("..", "other", "state"));
                break;
            case "linked db directory, '..' after a link in its target":
                Directory.CreateDirectory(Path.Combine(other, "nested"));
                Directory.CreateDirectory(Path.Combine(other, "store"));
                Directory.CreateDirectory(Path.Combine(main, "ancient"));
                Directory.CreateSymbolicLink(Path.Combine(main, "hop"), Path.Combine("..", "other", "nested"));
                // main/hop/.. is other, not main as the text suggests
                Directory.CreateSymbolicLink(db, Path.Combine("hop", "..", "store"));
                Directory.CreateSymbolicLink(Path.Combine(other, "store", "state"), Path.Combine("..", "..", "main", "ancient"));
                break;
            case "linked parent directory":
                Directory.CreateDirectory(Path.Combine(other, "db"));
                Directory.CreateSymbolicLink(main, "other");
                break;
            default:
                Directory.CreateSymbolicLink(Path.Combine(db, "dangling"), Path.Combine(temp.Path, "missing"));
                Directory.CreateSymbolicLink(Path.Combine(db, "loop"), Path.Combine(db, "loop"));
                break;
        }

        IDriveInfo mainDrive = Drive(main), otherDrive = Drive(other);
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IDirectoryInfoFactory directories = new RealFileSystem().DirectoryInfo;
        IDirectoryInfo aboveTemp = Dir(temp.Path);
        // The drive roots are spelled under the temp path, so a link above it, such as a linked TMPDIR, is not followed
        fileSystem.DirectoryInfo.New(Arg.Any<string>()).Returns(call =>
            call.Arg<string>().StartsWith(temp.Path, StringComparison.Ordinal) ? directories.New(call.Arg<string>()) : aboveTemp);
        fileSystem.DriveInfo.GetDrives().Returns(new[] { otherDrive, mainDrive });

        string dbPath = link.EndsWith("trailing separator", StringComparison.Ordinal) ? db + Path.DirectorySeparatorChar : db;
        Assert.That(fileSystem.GetDriveInfos(dbPath), Is.EqualTo(link switch
        {
            "linked db directory, '..' after a link in its target" => new[] { otherDrive, mainDrive },
            _ when linkedDb || linkedParent => new[] { otherDrive },
            "dangling and looping" => new[] { mainDrive },
            _ => new[] { mainDrive, otherDrive },
        }));
    }

    private static IFileSystem FileSystem(IDirectoryInfo top, IDirectoryInfo[] children, params IDriveInfo[] drives)
    {
        top.EnumerateDirectories().Returns(children);
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IDirectoryInfo notLinked = Dir(Root);
        fileSystem.DirectoryInfo.New(Arg.Any<string>()).Returns(notLinked);
        foreach (IDirectoryInfo directory in children.Append(top))
        {
            string fullName = directory.FullName;
            fileSystem.DirectoryInfo.New(fullName).Returns(directory);
        }
        fileSystem.DriveInfo.GetDrives().Returns(drives);
        return fileSystem;
    }

    private static IDriveInfo[] GetDriveInfosLoggingTo(InterfaceLogger logger, IFileSystem fileSystem)
    {
        logger.IsWarn.Returns(true);
        ILogManager originalLogManager = Static.LogManager;
        Static.LogManager = new OneLoggerLogManager(new ILogger(logger));
        try
        {
            return fileSystem.GetDriveInfos(Db);
        }
        finally
        {
            Static.LogManager = originalLogManager;
        }
    }

    private static IDirectoryInfo Dir(string fullName, string? linkTarget = null, string? target = null, bool exists = true)
    {
        IFileSystemInfo? resolved = Info(target);
        IDirectoryInfo directory = Substitute.For<IDirectoryInfo>();
        directory.FullName.Returns(fullName);
        directory.Exists.Returns(exists);
        directory.LinkTarget.Returns(linkTarget);
        directory.ResolveLinkTarget(Arg.Any<bool>()).Returns(resolved);
        return directory;
    }

    private static IFileSystemInfo? Info(string? fullName)
    {
        if (fullName is null) return null;
        IFileSystemInfo info = Substitute.For<IFileSystemInfo>();
        info.FullName.Returns(fullName);
        return info;
    }

    private static IDriveInfo Drive(string root)
    {
        IDriveInfo drive = Substitute.For<IDriveInfo>();
        drive.RootDirectory.FullName.Returns(root);
        return drive;
    }
}
