// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// Stdout carries nethtest's <c>--jsonout</c> results document, so anything else written there
/// corrupts it.
/// </summary>
/// <remarks>
/// The workflow parses the captured stdout with <c>jq</c>; when that fails it falls back to a
/// stderr-derived summary that has no per-error grouping and reports the run as a crash. The
/// post-state comparison used to print its truncation notice to stdout once a test had gathered
/// more than eight differences, which corrupted the results artifact of every run containing such
/// a test. The runner is driven as a process here because the assertions inside it are NUnit ones,
/// and those mark the surrounding test failed even when the runner catches them.
/// </remarks>
[TestFixture]
public class BlockchainTestJsonOutputTests
{
    /// <summary>Enough mismatching accounts to carry the comparison past its eight-difference truncation threshold.</summary>
    private const int MismatchingAccounts = 12;

    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public async Task Stdout_stays_parseable_json_when_a_test_reports_more_than_8_differences()
    {
        (string stdout, string stderr) = await RunNethtest(WriteFixture());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stderr, Does.Contain("More than 8 differences"),
                "the fixture has to reach the truncation branch, otherwise this test proves nothing");
            Assert.That(() => JsonDocument.Parse(stdout).Dispose(), Throws.Nothing,
                $"stdout must stay a parseable results document, was: {Trim(stdout)}");
            Assert.That(ResultCount(stdout), Is.EqualTo(1));
        }
    }

    private static int ResultCount(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        return document.RootElement.GetArrayLength();
    }

    private static string Trim(string output) => output.Length <= 200 ? output : $"{output[..200]}...";

    /// <summary>Runs the built nethtest binary over a fixture the way the nethtest workflow does.</summary>
    private static async Task<(string Stdout, string Stderr)> RunNethtest(string fixture)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "nethtest.exe" : "nethtest");
        Assert.That(File.Exists(executable), $"nethtest was not built next to the tests at {executable}");

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable, ["--blockTest", "--input", fixture, "--jsonout", "--neverTrace"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        // Both streams are drained before waiting, so neither can fill its buffer and block the run.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (await stdout, await stderr);
    }

    /// <summary>
    /// Writes a fixture whose post-state expects accounts that its empty pre-state never creates, so
    /// every one of them is a difference. It declares no blocks, which keeps the head at genesis and
    /// the comparison independent of block execution.
    /// </summary>
    private string WriteFixture()
    {
        TestBlockHeaderJson genesis = GenesisHeader();
        string file = Path.Combine(_directory, "more_than_8_differences.json");
        File.WriteAllText(file, $$"""
            {
              "more_than_8_differences": {
                "network": "Berlin",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "blocks": [],
                "lastblockhash": "{{genesis.Hash}}",
                "pre": {},
                "postState": {{MismatchingPostState()}}
              }
            }
            """);

        return file;
    }

    private static string MismatchingPostState()
    {
        StringBuilder postState = new("{");
        for (int i = 1; i <= MismatchingAccounts; i++)
        {
            postState
                .Append(i == 1 ? "" : ",")
                .Append($$"""
                    "0x{{i:x40}}": { "balance": "0x01", "code": "0x", "nonce": "0x00", "storage": {} }
                    """);
        }

        return postState.Append('}').ToString();
    }

    private static TestBlockHeaderJson GenesisHeader()
    {
        TestBlockHeaderJson header = new()
        {
            Bloom = Bloom.Empty.Bytes.ToHexString(true),
            Coinbase = Address.Zero.ToString(),
            Difficulty = "0x020000",
            ExtraData = "0x",
            GasLimit = "0x0f4240",
            GasUsed = "0x00",
            Hash = Keccak.Zero.ToString(),
            MixHash = Keccak.Zero.ToString(),
            Nonce = "0x0000000000000000",
            Number = "0x00",
            ParentHash = Keccak.Zero.ToString(),
            ReceiptTrie = Keccak.EmptyTreeHash.ToString(),
            StateRoot = Keccak.EmptyTreeHash.ToString(),
            Timestamp = "0x00",
            TransactionsTrie = Keccak.EmptyTreeHash.ToString(),
            UncleHash = Keccak.OfAnEmptySequenceRlp.ToString()
        };

        // The runner rejects a genesis header whose declared hash is not the one it derives, so take
        // the hash from the same encode/decode round trip it uses.
        header.Hash = Rlp.Decode<Block>(Rlp.Encode(new Block(JsonToEthereumTest.Convert(header))).Bytes).Header.Hash!.ToString();
        return header;
    }
}
