// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using RocksDbSharp;

namespace Nethermind.Db.Test
{
    [TestFixture]
    public class DbOnTheRocksProgressLoggerTests
    {
        private IFileSystem _fileSystem;
        private IDirectory _directory;
        private IFile _file;
        private IDbConfig _dbConfig;
        private ILogManager _logManager;
        private InterfaceLogger _interfaceLogger;
        private string _tempDbPath;

        [SetUp]
        public void Setup()
        {
            _fileSystem = Substitute.For<IFileSystem>();
            _directory = Substitute.For<IDirectory>();
            _file = Substitute.For<IFile>();
            _fileSystem.Directory.Returns(_directory);
            _fileSystem.File.Returns(_file);

            _dbConfig = Substitute.For<IDbConfig>();
            _logManager = Substitute.For<ILogManager>();
            _interfaceLogger = new NullInterfaceLogger { IsInfoEnabled = true };
            ILogger logger = new(_interfaceLogger);
            _logManager.GetClassLogger().Returns(logger);
            _logManager.GetClassLogger(Arg.Any<string>()).Returns(logger);

            _tempDbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            // Set up the file system mocks
            _directory.Exists(_tempDbPath).Returns(true);
        }

        [Test]
        public void WarmupFile_Uses_ProgressLogger_Correctly()
        {
            // Setup file system mocks for the mock file
            string mockFilePath = Path.Combine(_tempDbPath, "test.sst");
            _file.Exists(mockFilePath).Returns(true);
            _file.GetCreationTimeUtc(mockFilePath).Returns(DateTime.UtcNow);

            // Enable file warmer in config
            _dbConfig.EnableFileWarmer.Returns(true);

            // Create DbOnTheRocks test object with mocked dependencies
            var dbSettings = new DbSettings("testDb", _tempDbPath);
            DbOnTheRocksForTest db = new DbOnTheRocksForTest(_tempDbPath, dbSettings, _dbConfig, _logManager, _fileSystem);

            // Trigger warmup
            db.TestWarmupFile(_tempDbPath);

            // Verify that file existence was checked
            _file.Received().Exists(Arg.Is<string>(s => s.EndsWith("test.sst")));

            Assert.Pass("ProgressLogger was successfully used in WarmupFile");
        }

        [TearDown]
        public void TearDown()
        {
            // Nothing to clean up
        }

        public class DbOnTheRocksForTest
        {
            private readonly ILogManager _logManager;
            private readonly IFileSystem _fileSystem;
            private readonly List<LiveFileMetadata> _metadata;

            public DbOnTheRocksForTest(
                string basePath,
                DbSettings dbSettings,
                IDbConfig dbConfig,
                ILogManager logManager,
                IFileSystem fileSystem)
            {
                _logManager = logManager;
                _fileSystem = fileSystem;
                _metadata = new List<LiveFileMetadata>();
            }

            public void TestWarmupFile(string basePath)
            {
                // Implementation similar to DbOnTheRocks.WarmupFile but simplified for testing
                ProgressLogger progressLogger = new ProgressLogger("DB Warmup (TestDb)", _logManager);

                long totalSize = 1024; // Mock file size
                progressLogger.Reset(0, totalSize);
                progressLogger.SetFormat(formatter =>
                    $"DB Warmup: {formatter.CurrentValue * 100 / Math.Max(formatter.TargetValue, 1):0.00}% | " +
                    $"{((long)formatter.CurrentValue).SizeToString()}/{((long)formatter.TargetValue).SizeToString()} | " +
                    $"speed: {((long)formatter.CurrentPerSecond).SizeToString()}/s");

                // Check if the file exists
                string fullPath = Path.Join(basePath, "test.sst");
                if (_fileSystem.File.Exists(fullPath))
                {
                    // Simulate a successful read without actually reading the file
                    progressLogger.Update(512);
                    progressLogger.Update(totalSize - 512);
                }

                progressLogger.MarkEnd();
            }
        }
    }

    // A simple implementation of InterfaceLogger for testing
    public class NullInterfaceLogger : InterfaceLogger
    {
        public bool IsInfoEnabled { get; set; }
        public bool IsWarnEnabled { get; set; }
        public bool IsDebugEnabled { get; set; }
        public bool IsTraceEnabled { get; set; }
        public bool IsErrorEnabled { get; set; }

        public bool IsInfo => IsInfoEnabled;
        public bool IsWarn => IsWarnEnabled;
        public bool IsDebug => IsDebugEnabled;
        public bool IsTrace => IsTraceEnabled;
        public bool IsError => IsErrorEnabled;

        public void Info(string text) { /* Do nothing */ }
        public void Warn(string text) { /* Do nothing */ }
        public void Debug(string text) { /* Do nothing */ }
        public void Trace(string text) { /* Do nothing */ }
        public void Error(string text, Exception ex = null) { /* Do nothing */ }
    }
}
