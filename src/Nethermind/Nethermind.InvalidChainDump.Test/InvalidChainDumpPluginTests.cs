// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.InvalidChainDump.Test;

public class InvalidChainDumpPluginTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Invalid_block_diagnostics_are_uploaded_as_one_archive([Values] bool shutDownOnRejection)
    {
        CapturingStore store = new();
        string[] dumps = [];
        try
        {
            BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
                .Intercept<IInitConfig>(static initConfig => initConfig.AutoDump = DumpOptions.None)
                .AddModule(new InvalidChainDumpModule())
                .AddSingleton<IInvalidChainDumpConfig>(new InvalidChainDumpConfig { ServiceUrl = "http://127.0.0.1:9000", AccessKey = "key", SecretKey = "secret" })
                .AddSingleton<IDumpArchiveStore>(store));
            Block invalid;
            try
            {
                Block head = await chain.AddBlock();
                invalid = Build.A.Block.WithParent(head).WithGasLimit(head.GasLimit).WithBaseFeePerGas(head.BaseFeePerGas)
                    .WithStateRoot(Keccak.Zero).TestObject;
                dumps = [$"block_{invalid.Hash}.rlp", $"receipts_{invalid.Hash}_failed.txt", $"parityStyle_{invalid.Hash}_failed.txt", $"gethStyle_{invalid.Hash}_failed.txt"];

                using ManualResetEventSlim rejected = new();
                chain.MainProcessingContext.BlockProcessingQueue.BlockRemoved += (_, e) =>
                {
                    if (e.BlockHash == invalid.Hash && e.ProcessingResult != ProcessingResult.Success) rejected.Set();
                };
                chain.BlockTree.SuggestBlock(invalid);
                Assert.That(rejected.Wait(Wait), Is.True, "the block should be rejected");
                if (!shutDownOnRejection) await store.Uploaded.WaitAsync(Wait);
            }
            finally
            {
                chain.Dispose();
            }

            Assert.That(store.Uploaded.IsCompletedSuccessfully, Is.True, "shutdown should not complete before the dump is uploaded");
            (string[] entries, string manifest) = ReadArchive(await store.Uploaded);
            string[] allEntries = [.. dumps, InvalidChainDumpUploader.ManifestFileName];
            using (Assert.EnterMultipleScope())
            {
                // A disposing container can no longer open the replay env, so a shutdown only guarantees the block RLP.
                Assert.That(entries, shutDownOnRejection
                    ? Is.SupersetOf(new[] { dumps[0], InvalidChainDumpUploader.ManifestFileName }).And.SubsetOf(allEntries)
                    : Is.EqualTo(allEntries));
                Assert.That(manifest, Does.Contain($"hash={invalid.Hash}").And.Contain($"number={invalid.Number}"));
                Assert.That(store.ArchivePaths.Any(File.Exists), Is.False, "an uploaded archive should be deleted");
            }
        }
        finally
        {
            foreach (string dump in dumps) File.Delete(Path.Combine(Path.GetTempPath(), dump));
        }
    }

    [Test]
    public async Task Failed_upload_does_not_stop_later_uploads([Values] bool timesOut)
    {
        using TempPath dump = TempPath.GetTempFile();
        await File.WriteAllTextAsync(dump.Path, "trace");
        CapturingStore store = new(failures: 1, hang: timesOut);
        Block first = Build.A.Block.WithNumber(1).TestObject;
        Block second = Build.A.Block.WithNumber(2).TestObject;

        try
        {
            using (InvalidChainDumpUploader uploader = new(new InvalidChainDumpConfig { UploadTimeoutMs = 100 }, store, LimboLogs.Instance))
            {
                uploader.OnDumped(first, [dump.Path]);
                uploader.OnDumped(second, [dump.Path]);
            }

            Assert.That(store.Uploaded.IsCompletedSuccessfully, Is.True, "disposal should let the queued upload run after the failed one");
            (string[] entries, string manifest) = ReadArchive(await store.Uploaded);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(entries, Is.EqualTo(new[] { Path.GetFileName(dump.Path), InvalidChainDumpUploader.ManifestFileName }));
                Assert.That(manifest, Does.Contain($"hash={second.Hash}"));
                Assert.That(File.Exists(store.ArchivePaths.First()), Is.True, "a failed upload should keep its archive");
            }
        }
        finally
        {
            foreach (string archivePath in store.ArchivePaths) File.Delete(archivePath);
        }
    }

    [TestCase("", "key", "secret")]
    [TestCase("ftp://127.0.0.1", "key", "secret")]
    [TestCase("http://127.0.0.1:9000", "", "secret")]
    [TestCase("http://127.0.0.1:9000", "key", " ")]
    public void Incomplete_s3_configuration_is_rejected(string serviceUrl, string accessKey, string secretKey) =>
        Assert.That(
            () => new S3DumpArchiveStore(new InvalidChainDumpConfig { ServiceUrl = serviceUrl, AccessKey = accessKey, SecretKey = secretKey }),
            Throws.TypeOf<InvalidConfigurationException>());

    private static (string[] Entries, string Manifest) ReadArchive(byte[] archiveBytes)
    {
        using ZipArchive archive = new(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        using StreamReader manifest = new(archive.GetEntry(InvalidChainDumpUploader.ManifestFileName)!.Open());
        return ([.. archive.Entries.Select(static entry => entry.FullName)], manifest.ReadToEnd());
    }

    /// <summary>Fails the first <c>failures</c> uploads, by hanging until cancelled when <c>hang</c> is set, and captures the next one.</summary>
    private sealed class CapturingStore(int failures = 0, bool hang = false) : IDumpArchiveStore
    {
        private readonly TaskCompletionSource<byte[]> _uploaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public ConcurrentQueue<string> ArchivePaths { get; } = new();

        public Task<byte[]> Uploaded => _uploaded.Task;

        public async Task<string> UploadAsync(string archivePath, CancellationToken cancellationToken)
        {
            ArchivePaths.Enqueue(archivePath);
            if (Interlocked.Increment(ref _calls) <= failures)
            {
                if (hang) await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new IOException("Upload failed.");
            }

            _uploaded.TrySetResult(await File.ReadAllBytesAsync(archivePath, cancellationToken));
            return Path.GetFileName(archivePath);
        }
    }
}
