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

        V8ScriptEngine engine = new(V8ScriptEngineFlags.EnableTaskPromiseConversion);
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
    public void Erc20ContractsFromLogs_KeepsFreshErc20AndDropsNftsKnownAndIgnored()
    {
        using V8ScriptEngine engine = CreateEngine();
        string fresh = "0x" + new string('a', 40);
        string nft = "0x" + new string('b', 40);
        string known = "0x" + new string('c', 40);
        string ignored = "0x" + new string('d', 40);
        // four Transfer logs: a fresh ERC-20 (3 topics, kept), an ERC-721 (4 topics, dropped),
        // an already-tracked ERC-20 (in known), and one the user hid (in ignored)
        string logs =
            $"[{{ address: '{fresh}', topics: ['t','f','to'] }}," +
            $" {{ address: '{nft}', topics: ['t','f','to','id'] }}," +
            $" {{ address: '{known}', topics: ['t','f','to'] }}," +
            $" {{ address: '{ignored}', topics: ['t','f','to'] }}]";
        object result = engine.Evaluate(
            $"erc20ContractsFromLogs({logs}, new Set(['{known}']), new Set(['{ignored}'])).join(',')");
        Assert.That(result, Is.EqualTo(fresh));
    }

    [TestCase("0xe8d4a51000", 0, "1T")]       // 1e12 tokens -> compact, can't blow out the layout
    public void FormatUnits_CompactsAstronomicalAmounts(string hexValue, int decimals, string expected)
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.That(engine.Evaluate($"formatUnits('{hexValue}', {decimals})"), Is.EqualTo(expected));
    }

    [Test]
    public void LooksLikeSpam_FlagsPromoUrlAndAbsurdTokens()
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate("looksLikeSpam('claim at reward.xyz', 18, null)"), Is.True, "url-ish");
            Assert.That(engine.Evaluate("looksLikeSpam('AIRDROP $50', 18, null)"), Is.True, "promo words");
            Assert.That(engine.Evaluate("looksLikeSpam('OK', 0, '0x' + (9n*10n**33n).toString(16))"), Is.True, "airdrop-scale balance");
            Assert.That(engine.Evaluate("looksLikeSpam('USDC', 6, '0x64')"), Is.False, "legit stablecoin");
            Assert.That(engine.Evaluate("looksLikeSpam('PEPE', 18, '0x' + (4200n*10n**18n).toString(16))"), Is.False, "legit token");
        });
    }

    [Test]
    public void CurrencySymbol_ShortensDisambiguatedSymbolsExceptInTheFullForm()
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.Multiple(() =>
        {
            // dollar-family: full symbol carries the country prefix, per-row collapses to '$'
            engine.Execute("currency='MXN'");
            Assert.That(engine.Evaluate("currencySymbol(true)"), Is.EqualTo("MX$"), "full");
            Assert.That(engine.Evaluate("currencySymbol(false)"), Is.EqualTo("$"), "short");
            engine.Execute("currency='CNY'");
            Assert.That(engine.Evaluate("currencySymbol(true)"), Is.EqualTo("CN¥"), "full");
            Assert.That(engine.Evaluate("currencySymbol(false)"), Is.EqualTo("¥"), "short");
            // currencies without a prefix are identical in both forms
            engine.Execute("currency='EUR'");
            Assert.That(engine.Evaluate("currencySymbol(false)"), Is.EqualTo("€"));
            Assert.That(engine.Evaluate("currencySymbol(true)"), Is.EqualTo("€"));
        });
    }

    [Test]
    public void RpcBatch_SplitsOversizedBatchesToRespectServerLimit()
    {
        using V8ScriptEngine engine = CreateEngine();
        // capture each sub-batch size and echo {id, result} so results map back 1:1 in input order
        engine.Execute("""
            globalThis.__chunks = [];
            globalThis.fetch = function (path, opts) {
                const body = JSON.parse(opts.body);
                __chunks.push(body.length);
                const arr = body.map((r) => ({ jsonrpc: '2.0', id: r.id, result: r.params[0].to }));
                return Promise.resolve({ json: () => Promise.resolve(arr) });
            };
            """);
        // 1200 calls exceed Nethermind's 1024 batch cap: must split (500,500,200) and preserve order
        System.Threading.Tasks.Task<object> task = (System.Threading.Tasks.Task<object>)engine.Evaluate("""
            (async () => {
                const calls = Array.from({ length: 1200 }, (_, i) => ['eth_call', [{ to: '0x' + i }]]);
                const out = await rpcBatch(calls, '/');
                return JSON.stringify({
                    chunks: __chunks,
                    len: out.length,
                    ordered: out.every((v, i) => v === '0x' + i),
                });
            })()
            """);
        string json = (string)task.Result;
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"chunks\":[500,500,200]"), "split into server-safe sub-batches");
            Assert.That(json, Does.Contain("\"len\":1200"), "every call answered");
            Assert.That(json, Does.Contain("\"ordered\":true"), "results stay in input order");
        });
    }

    [Test]
    public void PriceProbe_RoutesStandaloneThenRegistry()
    {
        using V8ScriptEngine engine = CreateEngine();
        // XAUt has no registry entry — its first call must target the standalone XAU/USD aggregator
        object xaut = engine.Evaluate("priceProbe(CHAINS[1], '0x68749665ff8d2d112fa859aa293f07a622782f38').calls[0][1][0].to.toLowerCase()");
        // DAI is in the registry — its first call must use the registry latestRoundData(base,quote) selector
        object dai = engine.Evaluate("priceProbe(CHAINS[1], '0x6b175474e89094c44da98b954eedeac495271d0f').calls[0][1][0].data.slice(0,10)");
        Assert.Multiple(() =>
        {
            Assert.That(xaut, Is.EqualTo("0x214ed9da11d2fbe465a6fc601a91e62ebec1a0d6"), "XAUt → XAU/USD standalone feed");
            Assert.That(dai, Is.EqualTo("0xbcfd032d"), "DAI → registry latestRoundData(base,quote)");
        });
    }

    [Test]
    public void PriceProbe_DerivesWstEthFromStEthTimesWrapRatio()
    {
        using V8ScriptEngine engine = CreateEngine();
        // wstETH = stETH/USD × stEthPerToken(): registry price + feed-decimals + rate = 3 calls (decimals uncached)
        object result = engine.Evaluate("""
            (function () {
                const now = Math.floor(Date.now() / 1000);
                const w = (h) => h.replace(/^0x/, '').padStart(64, '0');
                const round = (ans) => '0x' + w('1') + w(ans) + w(now.toString(16)) + w(now.toString(16)) + w('1');
                const probe = priceProbe(CHAINS[1], '0x7f39c581f595b53c5cb19bd0b3f8da6c935e2ca0');
                const base = round((2000n * 10n ** 8n).toString(16)); // stETH = $2000 (8-dec feed)
                const dec = '0x' + w((8).toString(16));
                const rate = '0x' + w((12n * 10n ** 17n).toString(16)); // 1.2 wstETH→stETH (18 dec)
                return JSON.stringify({ calls: probe.calls.length, price: probe.combine([base, dec, rate]).toString() });
            })()
            """);
        // 2000 × 1.2 = $2400.00000000
        Assert.That(result, Is.EqualTo("{\"calls\":3,\"price\":\"240000000000\"}"));
    }

    [Test]
    public void PriceProbe_NormalizesNon8DecimalFeeds()
    {
        using V8ScriptEngine engine = CreateEngine();
        // DOLO/USD is an 18-decimal registry feed; without normalizing it reads 10^10 too high. The probe
        // fetches the feed's decimals (price + decimals = 2 calls) and scales to PRICE_DECIMALS.
        object result = engine.Evaluate("""
            (function () {
                const now = Math.floor(Date.now() / 1000);
                const w = (h) => h.replace(/^0x/, '').padStart(64, '0');
                const round = (ans) => '0x' + w('1') + w(ans) + w(now.toString(16)) + w(now.toString(16)) + w('1');
                const probe = priceProbe(CHAINS[1], '0x0f81001ef0a83ecce5ccebf63eb302c70a39a654'); // DOLO
                const price = round((225n * 10n ** 14n).toString(16)); // $0.0225 at 18 decimals (2.25e16)
                const dec = '0x' + w((18).toString(16));
                return JSON.stringify({ calls: probe.calls.length, price: probe.combine([price, dec]).toString() });
            })()
            """);
        // 2.25e16 (18-dec) → 2.25e6 (8-dec) = $0.0225
        Assert.That(result, Is.EqualTo("{\"calls\":2,\"price\":\"2250000\"}"));
    }

    [Test]
    public void ComputeV2Price8_PricesFromReservesAndRejectsThinPools()
    {
        using V8ScriptEngine engine = CreateEngine();
        // pool: 1,000,000 TOKEN vs 500 WETH -> 0.0005 WETH/token; ETH=$2000 -> $1.00 (deep enough: $1M liq)
        object priced = engine.Evaluate("""
            (function () {
                const w = (h) => h.replace(/^0x/, '').padStart(64, '0');
                const T = '0x000000000000000000000000000000000000000a';
                const reserves = '0x' + w((1000000n * 10n ** 18n).toString(16)) + w((500n * 10n ** 18n).toString(16)) + w('0');
                const quote = { addr: '0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2', decimals: 18 };
                const p = computeV2Price8(reserves, '0x' + w(T), T, 18, quote, 2000n * 10n ** 8n);
                return p === null ? 'null' : p.toString();
            })()
            """);
        Assert.That(priced, Is.EqualTo("100000000"), "0.0005 WETH x $2000 = $1.00 (8-dec)");

        // same ratio but only 0.1 WETH (~$200) of liquidity -> below the anti-spoofing floor -> rejected
        object thin = engine.Evaluate("""
            (function () {
                const w = (h) => h.replace(/^0x/, '').padStart(64, '0');
                const T = '0x000000000000000000000000000000000000000a';
                const reserves = '0x' + w((200000n * 10n ** 18n).toString(16)) + w((10n ** 17n).toString(16)) + w('0');
                const quote = { addr: '0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2', decimals: 18 };
                return computeV2Price8(reserves, '0x' + w(T), T, 18, quote, 2000n * 10n ** 8n) === null ? 'null' : 'priced';
            })()
            """);
        Assert.That(thin, Is.EqualTo("null"), "thin pool below the liquidity floor is rejected");
    }

    [Test]
    public void ComputeV3Price8_PricesFromSqrtPriceAndRejectsThinPools()
    {
        using V8ScriptEngine engine = CreateEngine();
        // token0 = TOKEN (18-dec), token1 = WETH (18-dec). sqrtPriceX96 = 2^96 => price(token1/token0)=1,
        // i.e. 1 WETH per TOKEN; ETH=$2000 => TOKEN = $2000. Deep pool (10 WETH ~$20k) -> priced; thin -> null.
        object result = engine.Evaluate("""
            (function () {
                const w = (h) => h.replace(/^0x/, '').padStart(64, '0');
                const T = '0x000000000000000000000000000000000000000a';
                const slot0 = '0x' + w((1n << 96n).toString(16)); // sqrtPriceX96 = 2^96
                const quote = { addr: '0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2', decimals: 18 };
                const deep = '0x' + w((10n * 10n ** 18n).toString(16));  // 10 WETH (~$20k) in pool
                const thin = '0x' + w((10n ** 17n).toString(16));        // 0.1 WETH (~$200)
                const priced = computeV3Price8(slot0, '0x' + w(T), T, 18, quote, 2000n * 10n ** 8n, deep);
                const rejected = computeV3Price8(slot0, '0x' + w(T), T, 18, quote, 2000n * 10n ** 8n, thin);
                return JSON.stringify({ priced: priced === null ? 'null' : priced.toString(), rejected: rejected === null ? 'null' : 'priced' });
            })()
            """);
        Assert.That(result, Is.EqualTo("{\"priced\":\"200000000000\",\"rejected\":\"null\"}")); // $2000.00000000, thin rejected
    }

    // Minimal DOM shim so the display-only fillThumbs can run under bare V8: el() needs
    // document.createElement + className/textContent, and the box needs append/replaceChildren.
    private const string DomShim = """
        globalThis.document = {
            createElement: (tag) => ({
                tag, className: '', textContent: '', src: null, title: '', onclick: null,
                children: [],
                append(...n) { this.children.push(...n); },
                replaceChildren(...n) { this.children = [...n]; },
            }),
        };
        globalThis.__mkThumbs = (n) => Array.from({ length: n }, (_, i) => ({ src: 'data:image/svg+xml,x', title: '#' + i }));
        globalThis.__summary = (box) => box.children.map((c) => c.tag === 'img' ? 'img' : c.textContent).join('|');
        globalThis.__click = (box, label) => box.children.find((c) => c.textContent === label).onclick();
        """;

    [Test]
    public void FillThumbs_TogglesBetweenPreviewAndAll()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute(DomShim);
        // 10 fully-loaded thumbs: collapsed shows MAX_NFT_THUMBS (4) + "+6 more"; expanding shows all 10 + "show less"
        object result = engine.Evaluate("""
            (function () {
                const node = { chainId: 1 };
                const collection = { address: '0x000000000000000000000000000000000000dEaD' };
                const cached = { count: 10, ids: __mkThumbs(10), thumbs: __mkThumbs(10), kind: 'enum721' };
                const box = document.createElement('div');
                fillThumbs(box, cached, node, '0xabc', collection);
                const collapsed = __summary(box);
                __click(box, '+6 more');
                const expanded = __summary(box);
                __click(box, 'show less');
                const recollapsed = __summary(box);
                return JSON.stringify({ collapsed, expanded, recollapsed });
            })()
            """);
        Assert.That(result, Is.EqualTo(
            "{\"collapsed\":\"img|img|img|img|+6 more\","
          + "\"expanded\":\"img|img|img|img|img|img|img|img|img|img|show less\","
          + "\"recollapsed\":\"img|img|img|img|+6 more\"}"));
    }

    [Test]
    public void FillThumbs_ShowsExpandBeforeBackgroundArtFinishesLoading()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute(DomShim);
        // regression: only the first row of art has loaded (thumbs == MAX_NFT_THUMBS) but 10 ids are known.
        // The "+N more" affordance must still appear (id-based), not wait for the background art to finish.
        object result = engine.Evaluate("""
            (function () {
                const node = { chainId: 1 };
                const collection = { address: '0x000000000000000000000000000000000000dEaD' };
                const box = document.createElement('div');
                fillThumbs(box, { count: 10, ids: __mkThumbs(10), thumbs: __mkThumbs(4), kind: 'enum721' }, node, '0xa', collection);
                return __summary(box);
            })()
            """);
        Assert.That(result, Is.EqualTo("img|img|img|img|+6 more"));
    }

    [Test]
    public void FillThumbs_NotesStillLoadingAndUndiscoverableHoldings()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute(DomShim);
        // expanded while art is still streaming (loading flag set) -> "loading…";
        // expanded when holdings exceed what node data could discover (5 shown, balanceOf 8) -> "3 more not shown"
        object result = engine.Evaluate("""
            (function () {
                const node = { chainId: 1 };
                const collection = { address: '0x000000000000000000000000000000000000dEaD' };
                const loadingBox = document.createElement('div');
                fillThumbs(loadingBox, { count: 10, ids: __mkThumbs(10), thumbs: __mkThumbs(10), kind: 'enum721', loading: true }, node, '0xa', collection);
                __click(loadingBox, '+6 more'); // affordance is id-based (10 ids - 4 shown)
                const loading = __summary(loadingBox);
                const truncBox = document.createElement('div');
                fillThumbs(truncBox, { count: 8, ids: __mkThumbs(5), thumbs: __mkThumbs(5), kind: 'enum721' }, node, '0xb', collection);
                __click(truncBox, '+1 more');
                const truncated = __summary(truncBox);
                return JSON.stringify({ loading, truncated });
            })()
            """);
        Assert.That(result, Is.EqualTo(
            "{\"loading\":\"img|img|img|img|img|img|img|img|img|img|show less|loading…\","
          + "\"truncated\":\"img|img|img|img|img|show less|3 more not shown\"}"));
    }

    [Test]
    public void ResetAutoDetected_DropsAutoAssetsButKeepsManualAndDefaults()
    {
        using V8ScriptEngine engine = CreateEngine();
        // unpinning must drop auto-detected tokens AND NFTs (the reported bug: auto NFTs were left behind),
        // reset tokens to the chain defaults, keep manually-tracked NFT collections, and clear the hidden list
        object result = engine.Evaluate("""
            (function () {
                const node = { meta: { defaults: [{ address: '0xdef', ticker: 'DEF' }] } };
                const state = {
                    detected: [{ address: '0xauto', ticker: 'AUTO', auto: true }],
                    nfts: [{ address: '0xnftauto', ticker: 'AUTO', auto: true }, { address: '0xnftman', ticker: 'MAN' }],
                    ignored: [{ address: '0xspam', ticker: 'SPAM' }],
                };
                resetAutoDetected(state, node);
                return JSON.stringify({
                    detected: state.detected.map((t) => t.ticker),
                    nfts: state.nfts.map((t) => t.ticker),
                    ignored: state.ignored.length,
                });
            })()
            """);
        Assert.That(result, Is.EqualTo("{\"detected\":[\"DEF\"],\"nfts\":[\"MAN\"],\"ignored\":0}"));
    }

    [Test]
    public void PruneEmptyNfts_DropsAutoCollectionsNoAccountHolds()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute("""
            nodes = [{ chainId: 1, enabled: true, meta: {} }];
            globalThis.mergedAddresses = () => [{ address: '0xowner' }];
            globalThis.renderTokens = () => {};
            globalThis.renderCards = () => {};
            """);
        // an auto ERC-1155 the account no longer holds (count 0, e.g. Unisocks received once then sold) is
        // dropped; a held auto collection, a manual one, and one whose holdings aren't cached yet are kept
        object result = engine.Evaluate("""
            (function () {
                const state = { nfts: [
                    { address: '0xMANUAL',  ticker: 'MAN' },
                    { address: '0xHELD',    ticker: 'HELD', auto: true },
                    { address: '0xEMPTY',   ticker: 'EMPTY', auto: true },
                    { address: '0xUNKNOWN', ticker: 'UNK', auto: true },
                ] };
                states.set(1, state);
                nftCache.set('0xowner|1|0xheld',  { count: 3, ids: [], thumbs: [], kind: 'erc1155' });
                nftCache.set('0xowner|1|0xempty', { count: 0, ids: [], thumbs: [], kind: 'erc1155' });
                pruneEmptyNfts();
                return state.nfts.map((n) => n.ticker).join(',');
            })()
            """);
        Assert.That(result, Is.EqualTo("MAN,HELD,UNK"));
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
