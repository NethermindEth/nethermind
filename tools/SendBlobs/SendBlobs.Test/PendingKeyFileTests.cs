// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using NUnit.Framework;
using SendBlobs;

namespace SendBlobs.Test;

[NonParallelizable]
public class PendingKeyFileTests
{
    private TempPath _workDir = null!;
    private string _targetPath = null!;
    private string _pendingPath = null!;

    [SetUp]
    public void SetUp()
    {
        _workDir = TempPath.GetTempDirectory();
        Directory.CreateDirectory(_workDir.Path);
        _targetPath = Path.Combine(_workDir.Path, "keys.txt");
        _pendingPath = _targetPath + ".pending";
    }

    [TearDown]
    public void TearDown() => _workDir.Dispose();

    [Test]
    public void Open_WhenTargetIsNonexistent_CreatesPendingFileWithSuffix()
    {
        using PendingKeyFile pending = PendingKeyFile.Open(_targetPath);

        Assert.That(File.Exists(_pendingPath), Is.True);
        Assert.That(File.Exists(_targetPath), Is.False);
        Assert.That(pending.TempPath, Is.EqualTo(_pendingPath));
    }

    [Test]
    public void Open_WhenPendingFileAlreadyExists_ThrowsIOException()
    {
        File.WriteAllText(_pendingPath, "stale-key-from-prior-run");

        IOException? thrown = Assert.Throws<IOException>(() => PendingKeyFile.Open(_targetPath));
        Assert.That(thrown!.Message, Does.Contain("already exists"));
        Assert.That(File.ReadAllText(_pendingPath), Is.EqualTo("stale-key-from-prior-run"));
    }

    [Test]
    public void AppendDurable_AfterWrite_LinePresentInPendingFile()
    {
        using PrivateKeyGenerator generator = new();
        PrivateKey key = generator.Generate();

        using PendingKeyFile pending = PendingKeyFile.Open(_targetPath);
        pending.AppendDurable(key);

        Assert.That(File.ReadAllText(_pendingPath), Is.EqualTo(key.ToString() + "\n"),
            "AppendDurable must fsync before returning");
    }

    [Test]
    public void CommitAtomic_AfterAppends_ReplacesTargetWithPendingContents()
    {
        File.WriteAllText(_targetPath, "old-content-that-must-be-overwritten\n");
        using PrivateKeyGenerator generator = new();
        PrivateKey key1 = generator.Generate();
        PrivateKey key2 = generator.Generate();
        string expected = key1.ToString() + "\n" + key2.ToString() + "\n";

        using (PendingKeyFile pending = PendingKeyFile.Open(_targetPath))
        {
            pending.AppendDurable(key1);
            pending.AppendDurable(key2);
            pending.CommitAtomic();
        }

        Assert.That(File.Exists(_pendingPath), Is.False);
        Assert.That(File.ReadAllText(_targetPath), Is.EqualTo(expected));
    }

    [Test]
    public void Dispose_WithoutCommit_LeavesPendingFileForRecovery()
    {
        const string original = "original-keys-that-must-not-be-touched\n";
        File.WriteAllText(_targetPath, original);
        using PrivateKeyGenerator generator = new();
        PrivateKey key = generator.Generate();

        PendingKeyFile pending = PendingKeyFile.Open(_targetPath);
        try { pending.AppendDurable(key); }
        finally { pending.Dispose(); }

        Assert.That(File.ReadAllText(_pendingPath), Is.EqualTo(key.ToString() + "\n"));
        Assert.That(File.ReadAllText(_targetPath), Is.EqualTo(original));
    }

    [TestCase(PostCommitOp.AppendAgain)]
    [TestCase(PostCommitOp.CommitAgain)]
    public void AfterCommit_OperationsThrowInvalidOperationException(PostCommitOp op)
    {
        using PrivateKeyGenerator generator = new();
        using PendingKeyFile pending = PendingKeyFile.Open(_targetPath);
        pending.AppendDurable(generator.Generate());
        pending.CommitAtomic();

        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (op)
            {
                case PostCommitOp.AppendAgain: pending.AppendDurable(generator.Generate()); break;
                case PostCommitOp.CommitAgain: pending.CommitAtomic(); break;
            }
        });
    }

    public enum PostCommitOp { AppendAgain, CommitAgain }
}
