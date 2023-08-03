// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Abstractions;
using System.Security.Cryptography;
using Nethermind.Core.Authentication;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Authentication;

[TestFixture]
public class JwtAuthenticationTests
{

    [Test]
    public void Test_use_existing_default_if_filePath_is_empty()
    {
        string filePath = "";
        string defaultFilePath = "/mocked/default/file/path/";

        ITimestamper timestamper = new Timestamper();
        ILogger logger = new TestLogger();

        IFileSystem fakeFileSystem = Substitute.For<IFileSystem>();
        IFileInfo fakeFileInfo = Substitute.For<IFileInfo>();
        IFileInfoFactory fakeFileInfoFactory = Substitute.For<IFileInfoFactory>();
        IJwtSecretPathResolver fakeJwtSecretPathResolver = Substitute.For<IJwtSecretPathResolver>();
        fakeJwtSecretPathResolver.GetDefaultFilePath().Returns(defaultFilePath);


        byte[] fileBytes = RandomNumberGenerator.GetBytes(64 / 2);
        string hexString = Bytes.ToHexString(fileBytes);
        byte[] hexStringBytes = System.Text.Encoding.UTF8.GetBytes(hexString);
        MemoryStream memoryStream = new(hexStringBytes);
        StreamReader streamReader = new(memoryStream);

        fakeFileInfo.Exists.Returns(true);
        fakeFileInfo.Length.Returns(1);

        fakeFileInfoFactory.New(defaultFilePath).Returns(fakeFileInfo);

        fakeFileSystem.FileInfo.Returns(fakeFileInfoFactory);

        fakeFileSystem.File.OpenText(defaultFilePath).Returns(streamReader);

        JwtAuthentication.FromFile(filePath, timestamper, logger, fakeFileSystem, fakeJwtSecretPathResolver);

        fakeFileInfoFactory.Received().New(defaultFilePath);
        fakeFileSystem.File.Received().OpenText(defaultFilePath);
    }

    [Test]
    public void Test_use_fallback_existing_if_filePath_is_empty()
    {
        string filePath = "";
        string defaultFilePath = "/mocked/default/file/path/";
        string oldDefault = "keystore/jwt-secret";

        ITimestamper timestamper = new Timestamper();
        ILogger logger = new TestLogger();

        IFileSystem fakeFileSystem = Substitute.For<IFileSystem>();
        IFileInfo fakeDefaultFileInfo = Substitute.For<IFileInfo>();
        IFileInfo fakeOldDefaultFileInfo = Substitute.For<IFileInfo>();
        IFileInfoFactory fakeFileInfoFactory = Substitute.For<IFileInfoFactory>();
        IJwtSecretPathResolver fakeJwtSecretPathResolver = Substitute.For<IJwtSecretPathResolver>();
        fakeJwtSecretPathResolver.GetDefaultFilePath().Returns(defaultFilePath);


        byte[] fileBytes = RandomNumberGenerator.GetBytes(64 / 2);
        string hexString = Bytes.ToHexString(fileBytes);
        byte[] hexStringBytes = System.Text.Encoding.UTF8.GetBytes(hexString);
        MemoryStream memoryStream = new(hexStringBytes);
        StreamReader streamReader = new(memoryStream);

        fakeDefaultFileInfo.Exists.Returns(false);
        fakeOldDefaultFileInfo.Exists.Returns(true);
        fakeOldDefaultFileInfo.Length.Returns(1);

        fakeFileInfoFactory.New(oldDefault).Returns(fakeOldDefaultFileInfo);
        fakeFileInfoFactory.New(defaultFilePath).Returns(fakeDefaultFileInfo);

        fakeFileSystem.FileInfo.Returns(fakeFileInfoFactory);

        fakeFileSystem.File.OpenText(oldDefault).Returns(streamReader);

        JwtAuthentication.FromFile(filePath, timestamper, logger, fakeFileSystem, fakeJwtSecretPathResolver);

        fakeFileInfoFactory.Received().New(oldDefault);
        fakeFileSystem.File.Received().OpenText(oldDefault);
    }

    [Test]
    public void Test_generate_new_secret_with_default_if_filePath_is_empty()
    {
        string filePath = "";
        string defaultFilePath = "/mocked/default/file/path/";
        string oldDefault = "keystore/jwt-secret";

        ITimestamper timestamper = new Timestamper();
        ILogger logger = new TestLogger();

        IFileSystem fakeFileSystem = Substitute.For<IFileSystem>();
        IFileInfo fakeDefaultFileInfo = Substitute.For<IFileInfo>();
        IFileInfo fakeOldDefaultFileInfo = Substitute.For<IFileInfo>();
        IFileInfoFactory fakeFileInfoFactory = Substitute.For<IFileInfoFactory>();
        IJwtSecretPathResolver fakeJwtSecretPathResolver = Substitute.For<IJwtSecretPathResolver>();
        fakeJwtSecretPathResolver.GetDefaultFilePath().Returns(defaultFilePath);

        MemoryStream memoryStream = new();
        StreamWriter streamWriter = new(memoryStream);

        fakeDefaultFileInfo.Exists.Returns(false);
        fakeDefaultFileInfo.DirectoryName.Returns(defaultFilePath);
        fakeOldDefaultFileInfo.Exists.Returns(false);

        fakeFileInfoFactory.New(oldDefault).Returns(fakeOldDefaultFileInfo);
        fakeFileInfoFactory.New(defaultFilePath).Returns(fakeDefaultFileInfo);

        fakeFileSystem.FileInfo.Returns(fakeFileInfoFactory);

        fakeFileSystem.File.CreateText(defaultFilePath).Returns(streamWriter);

        JwtAuthentication.FromFile(filePath, timestamper, logger, fakeFileSystem, fakeJwtSecretPathResolver);

        fakeFileSystem.Directory.Received().CreateDirectory(defaultFilePath);
        Assert.That(memoryStream.ToArray().Length, Is.EqualTo(64));
    }

    [Test]
    public void Test_get_secret_from_provided_path_if_exist()
    {
        string filePath = "some/path/to/file";

        ITimestamper timestamper = new Timestamper();
        ILogger logger = new TestLogger();

        IFileSystem fakeFileSystem = Substitute.For<IFileSystem>();
        IFileInfo fakeFileInfo = Substitute.For<IFileInfo>();
        IFileInfoFactory fakeFileInfoFactory = Substitute.For<IFileInfoFactory>();


        byte[] fileBytes = RandomNumberGenerator.GetBytes(64 / 2);
        string hexString = Bytes.ToHexString(fileBytes);
        byte[] hexStringBytes = System.Text.Encoding.UTF8.GetBytes(hexString);
        MemoryStream memoryStream = new(hexStringBytes);
        StreamReader streamReader = new(memoryStream);

        fakeFileInfo.Exists.Returns(true);
        fakeFileInfo.Length.Returns(1);

        fakeFileInfoFactory.New(filePath).Returns(fakeFileInfo);

        fakeFileSystem.FileInfo.Returns(fakeFileInfoFactory);

        fakeFileSystem.File.OpenText(filePath).Returns(streamReader);

        JwtAuthentication.FromFile(filePath, timestamper, logger, fakeFileSystem, new JwtSecretPathResolver(new RuntimePlatformChecker()));

        fakeFileInfoFactory.Received().New(filePath);
        fakeFileSystem.File.Received().OpenText(filePath);
    }

    [Test]
    public void Test_generate_secret_from_provided_path_if_NOT_exist()
    {
        string filePath = "some/path/to/file";

        ITimestamper timestamper = new Timestamper();
        ILogger logger = new TestLogger();

        IFileSystem fakeFileSystem = Substitute.For<IFileSystem>();
        IFileInfo fakeFileInfo = Substitute.For<IFileInfo>();
        IFileInfoFactory fakeFileInfoFactory = Substitute.For<IFileInfoFactory>();

        MemoryStream memoryStream = new();
        StreamWriter streamWriter = new(memoryStream);

        fakeFileInfo.Exists.Returns(false);
        fakeFileInfo.DirectoryName.Returns(filePath);

        fakeFileInfoFactory.New(filePath).Returns(fakeFileInfo);

        fakeFileSystem.FileInfo.Returns(fakeFileInfoFactory);

        fakeFileSystem.File.CreateText(filePath).Returns(streamWriter);

        JwtAuthentication.FromFile(filePath, timestamper, logger, fakeFileSystem, new JwtSecretPathResolver(new RuntimePlatformChecker()));

        fakeFileSystem.Directory.Received().CreateDirectory(filePath);
        Assert.That(memoryStream.ToArray().Length, Is.EqualTo(64));
    }


}
