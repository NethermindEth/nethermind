// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Authentication;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Authentication;

[TestFixture]
public class JwtSecretPathResolverTests
{
    [Test]
    public void Test_get_filePath_for_linux_XDG_NOT_set()
    {
        IRuntimePlatformChecker fakeRuntimePlatformChecker = Substitute.For<IRuntimePlatformChecker>();
        fakeRuntimePlatformChecker.IsLinux().Returns(true);

        string originalXDG_DATA_HOME = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string originalHOME = Environment.GetEnvironmentVariable("HOME");

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
        Environment.SetEnvironmentVariable("HOME", "/home/user");

        try
        {
            JwtSecretPathResolver resolver = new(fakeRuntimePlatformChecker);

            Assert.That(resolver.GetDefaultFilePath(), Is.EqualTo("/home/user/.local/share/ethereum/engine/jwt.hex"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXDG_DATA_HOME);
            Environment.SetEnvironmentVariable("HOME", originalHOME);
        }
    }

    [Test]
    public void Test_get_filePath_for_linux_XDG_set()
    {
        IRuntimePlatformChecker fakeRuntimePlatformChecker = Substitute.For<IRuntimePlatformChecker>();
        fakeRuntimePlatformChecker.IsLinux().Returns(true);

        string originalXDG_DATA_HOME = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/my/XDG/path");

        try
        {
            JwtSecretPathResolver resolver = new(fakeRuntimePlatformChecker);

            Assert.That(resolver.GetDefaultFilePath(), Is.EqualTo("/my/XDG/path/ethereum/engine/jwt.hex"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXDG_DATA_HOME);
        }
    }

    [Test]
    public void Test_get_filePath_for_linux_HOME_and_XDG_not_set()
    {
        IRuntimePlatformChecker fakeRuntimePlatformChecker = Substitute.For<IRuntimePlatformChecker>();
        fakeRuntimePlatformChecker.IsLinux().Returns(true);

        string originalXDG_DATA_HOME = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string originalHOME = Environment.GetEnvironmentVariable("HOME");

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
        Environment.SetEnvironmentVariable("HOME", null);

        try
        {
            Exception? ex = Assert.Throws<Exception>(() => new JwtSecretPathResolver(fakeRuntimePlatformChecker).GetDefaultFilePath());
            Assert.That(ex?.Message, Is.EqualTo("HOME environment variable is not set"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXDG_DATA_HOME);
            Environment.SetEnvironmentVariable("HOME", originalHOME);
        }
    }

    [Test]
    public void Test_get_filePath_for_windows()
    {
        IRuntimePlatformChecker fakeRuntimePlatformChecker = Substitute.For<IRuntimePlatformChecker>();
        fakeRuntimePlatformChecker.IsWindows().Returns(true);

        string originalAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        JwtSecretPathResolver resolver = new(fakeRuntimePlatformChecker);

        Assert.That(resolver.GetDefaultFilePath(), Is.EqualTo($"{originalAppData}/Ethereum/Engine/jwt.hex"));
    }

    [Test]
    public void Test_get_filePath_for_osx()
    {
        IRuntimePlatformChecker fakeRuntimePlatformChecker = Substitute.For<IRuntimePlatformChecker>();
        fakeRuntimePlatformChecker.IsOSX().Returns(true);

        string originalHOME = Environment.GetEnvironmentVariable("HOME");

        Environment.SetEnvironmentVariable("HOME", "/home/user");

        try
        {
            JwtSecretPathResolver resolver = new(fakeRuntimePlatformChecker);

            Assert.That(resolver.GetDefaultFilePath(), Is.EqualTo("/home/user/Library/Application Support/Ethereum/Engine/jwt.hex"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHOME);
        }
    }

    [Test]
    public void Test_get_filePath_for_osx_HOME_not_set()
    {
        IRuntimePlatformChecker fakeRuntimePlatformChecker = Substitute.For<IRuntimePlatformChecker>();
        fakeRuntimePlatformChecker.IsOSX().Returns(true);

        string originalHOME = Environment.GetEnvironmentVariable("HOME");

        Environment.SetEnvironmentVariable("HOME", null);

        try
        {
            Exception? ex = Assert.Throws<Exception>(() => new JwtSecretPathResolver(fakeRuntimePlatformChecker).GetDefaultFilePath());
            Assert.That(ex?.Message, Is.EqualTo("HOME environment variable is not set"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHOME);
        }
    }
}
