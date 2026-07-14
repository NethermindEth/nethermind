// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.IO;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.FileProviders;
using Nethermind.BalanceViewer.Plugin;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

/// <summary>
/// Exercises the pure JavaScript logic embedded in the balance viewer page (keccak-256,
/// ENS namehash, EIP-55 checksums, ABI decoding, unit/fiat formatting, sync detection)
/// by loading the actual shipped balances.html into a V8 engine and asserting on its
/// functions — so the browser-side crypto and parsing are covered like any other code.
/// </summary>
[TestFixture]
public class BalanceViewerScriptTests
{
    // Minimal browser shims: the page guards its DOM entry point behind BALANCES_NO_AUTOINIT,
    // so only the top-level definitions run here. TextEncoder/TextDecoder are the sole Web
    // APIs the pure functions need and are absent from bare V8.
    private const string Shims = """
        globalThis.BALANCES_NO_AUTOINIT = true;
        globalThis.localStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
        globalThis.TextEncoder = function () {};
        TextEncoder.prototype.encode = function (s) {
            var u = unescape(encodeURIComponent(s)), a = new Uint8Array(u.length);
            for (var i = 0; i < u.length; i++) a[i] = u.charCodeAt(i);
            return a;
        };
        globalThis.TextDecoder = function () {};
        TextDecoder.prototype.decode = function (u8) {
            var s = '';
            for (var i = 0; i < u8.length; i++) s += String.fromCharCode(u8[i]);
            return decodeURIComponent(escape(s));
        };
        """;

    private static V8ScriptEngine CreateEngine()
    {
        IFileInfo page = new ManifestEmbeddedFileProvider(typeof(BalanceViewerPlugin).Assembly, "wwwroot").GetFileInfo("balances.html");
        Assert.That(page.Exists, Is.True, "balances.html is not embedded in the plugin assembly");

        using Stream stream = page.CreateReadStream();
        using StreamReader reader = new(stream);
        string html = reader.ReadToEnd();

        const string open = "<script>";
        int start = html.IndexOf(open, StringComparison.Ordinal);
        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0).And.LessThan(end), "could not locate the page script");
        string script = html.Substring(start + open.Length, end - start - open.Length);

        V8ScriptEngine engine = new();
        engine.Execute(Shims);
        engine.Execute(script);
        return engine;
    }

    private static string Word(string hex) => hex.PadLeft(64, '0');

    [TestCase("", "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")]
    [TestCase("abc", "4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45")]
    public void Keccak256_MatchesKnownVectors(string input, string expected)
    {
        using V8ScriptEngine engine = CreateEngine();
        object result = engine.Evaluate(
            $"Array.from(keccak256(new TextEncoder().encode('{input}')), b => ('0' + b.toString(16)).slice(-2)).join('')");
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("eth", "0x93cdeb708b7545dc668eb9280176169d1c33cfd8ed6f04690a0bcc88a93fc4ae")]
    [TestCase("vitalik.eth", "0xee6c4522aab0003e8d14cd40a6af439055fd2577951148c14b6cea9a53475835")]
    public void Namehash_MatchesEip137Vectors(string name, string expected)
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.That(engine.Evaluate($"namehash('{name}')"), Is.EqualTo(expected));
    }

    [Test]
    public void Checksum_AppliesEip55()
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.That(engine.Evaluate("checksum('0xd8da6bf26964af9d7eed9e03e53415d37aa96045')"),
            Is.EqualTo("0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045"));
    }

    [Test]
    public void DecodeAbiString_DecodesDynamicString()
    {
        using V8ScriptEngine engine = CreateEngine();
        // ABI-encoded "USDC": offset, length, right-padded data
        string encoded = "0x" + Word("20") + Word("4") + "55534443".PadRight(64, '0');
        Assert.That(engine.Evaluate($"decodeAbiString('{encoded}')"), Is.EqualTo("USDC"));
    }

    [Test]
    public void DecodeAbiString_DecodesLegacyBytes32Symbol()
    {
        using V8ScriptEngine engine = CreateEngine();
        // MKR-style bytes32 symbol(): "MKR" right-padded to 32 bytes
        string encoded = "0x" + "4d4b52".PadRight(64, '0');
        Assert.That(engine.Evaluate($"decodeAbiString('{encoded}')"), Is.EqualTo("MKR"));
    }

    [TestCase("0x14d1120d7b160000", 18, "1.5")]
    [TestCase("0x0", 18, "0")]
    [TestCase("0x1c6bf52634000", 18, "0.0005")]
    public void FormatUnits_ScalesByDecimals(string hexValue, int decimals, string expected)
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.That(engine.Evaluate($"formatUnits('{hexValue}', {decimals})"), Is.EqualTo(expected));
    }

    [Test]
    public void ParseBatchIds_ExtractsErc1155BatchTokenIds()
    {
        using V8ScriptEngine engine = CreateEngine();
        // TransferBatch data: ids array at offset 0x40 holding [5, 6]
        string data = "0x" + Word("40") + Word("a0") + Word("2") + Word("5") + Word("6");
        Assert.That(engine.Evaluate($"parseBatchIds('{data}').map(x => x.toString()).join(',')"), Is.EqualTo("5,6"));
    }

    [Test]
    public void IsNodeSyncing_JudgesByHeadAge()
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate("isNodeSyncing(false, { number: '0x0' })"), Is.True, "genesis head");
            Assert.That(engine.Evaluate("isNodeSyncing(false, { number: '0x10', timestamp: '0x' + Math.floor(Date.now()/1000 - 10).toString(16) })"),
                Is.False, "fresh head is synced");
            Assert.That(engine.Evaluate("isNodeSyncing(false, { number: '0x10', timestamp: '0x' + Math.floor(Date.now()/1000 - 99999).toString(16) })"),
                Is.True, "stale head is behind the tip");
        });
    }
}
