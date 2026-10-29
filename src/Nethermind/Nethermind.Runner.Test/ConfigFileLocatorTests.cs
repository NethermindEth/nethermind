// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class ConfigFileLocatorTests
{
    private string _configsDir = null!;
    private string _name = null!;
    private string _currentDirFile = null!;

    [SetUp]
    public void Setup()
    {
        _configsDir = Path.Combine(Path.GetTempPath(), $"nethermind-test-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(_configsDir);
        _name = $"config-{Path.GetRandomFileName()}";
        _currentDirFile = Path.GetFullPath($"{_name}.json");
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_configsDir, recursive: true);
        File.Delete(_currentDirFile);
    }

    [TestCase(".json")]
    [TestCase("")]
    public void Configs_dir_is_searched_before_current_directory(string extension)
    {
        string inConfigsDir = Path.Combine(_configsDir, $"{_name}.json");
        File.WriteAllText(inConfigsDir, "{}");
        File.WriteAllText(_currentDirFile, "{}");

        Assert.That(ConfigFileLocator.Find(_name + extension, _configsDir), Is.EqualTo(Path.GetFullPath(inConfigsDir)));
    }

    [Test]
    public void Falls_back_to_current_directory()
    {
        File.WriteAllText(_currentDirFile, "{}");

        Assert.That(ConfigFileLocator.Find($"{_name}.json", _configsDir), Is.EqualTo(_currentDirFile));
    }

    [Test]
    public void Returns_null_when_no_file_matches() =>
        Assert.That(ConfigFileLocator.Find(_name, _configsDir), Is.Null);

    [TestCase("configs/mainnet.json", true)]
    [TestCase("mainnet.cfg", true)]
    [TestCase("nonexistent.json", false)]
    public void Not_found_message_suggests_built_in_name_for_removed_forms(string config, bool suggests)
    {
        string message = ConfigFileLocator.NotFoundMessage(config);

        Assert.That(message, Does.StartWith($"Configuration '{config}' not found."));
        Assert.That(message.Contains("Did you mean '--config mainnet'?"), Is.EqualTo(suggests));
    }
}
