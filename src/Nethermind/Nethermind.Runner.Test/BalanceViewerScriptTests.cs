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
/// by loading the actual shipped portfolio.html into a V8 engine and asserting on its
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
        IFileInfo page = new ManifestEmbeddedFileProvider(typeof(BalanceViewerPlugin).Assembly, "wwwroot").GetFileInfo("portfolio.html");
        Assert.That(page.Exists, Is.True, "portfolio.html is not embedded in the plugin assembly");

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
                setAttribute(k, v) { this[k] = v; },
            }),
        };
        globalThis.location = { href: '', hash: '', pathname: '/', search: '', hostname: 'localhost', port: '' };
        globalThis.history = { replaceState: () => {}, pushState: () => {} };
        globalThis.__mkThumbs = (n) => Array.from({ length: n }, (_, i) => ({ src: 'data:image/svg+xml,x', title: '#' + i }));
        globalThis.__summary = (box) => box.children.map((c) => c.tag === 'img' ? 'img' : c.textContent).join('|');
        globalThis.__click = (box, label) => box.children.find((c) => c.textContent === label).onclick();
        """;

    [Test]
    public void FillThumbs_ShowsPreviewAndViewAllLink()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute(DomShim);
        // inline shows up to MAX_NFT_THUMBS (4) thumbs + a "view all N" link (N from the total count); a small
        // collection (<= 4) shows no link; the loading flag adds a "loading…" note
        object result = engine.Evaluate("""
            (function () {
                const node = { chainId: 1 };
                const coll = { address: '0x000000000000000000000000000000000000dEaD' };
                const fill = (cached) => { const b = document.createElement('div'); fillThumbs(b, cached, node, '0xa', coll); return __summary(b); };
                const big = fill({ count: 579, ids: __mkThumbs(200), thumbs: __mkThumbs(4), kind: 'enum721' });
                const loading = fill({ count: 10, ids: __mkThumbs(10), thumbs: __mkThumbs(10), kind: 'enum721', loading: true });
                const small = fill({ count: 3, ids: __mkThumbs(3), thumbs: __mkThumbs(3), kind: 'enum721' });
                return JSON.stringify({ big, loading, small });
            })()
            """);
        Assert.That(result, Is.EqualTo(
            "{\"big\":\"img|img|img|img|view all 579\","
          + "\"loading\":\"img|img|img|img|view all 10|loading…\","
          + "\"small\":\"img|img|img\"}"));
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
    public void OpenArt_ShowsMetadataAndTraitsEvenWithoutArt()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute(DomShim + """
            globalThis.document.body = document.createElement('body');
            globalThis.document.addEventListener = () => {};
            globalThis.document.removeEventListener = () => {};
            globalThis.__texts = (n) => { let o = []; for (const c of (n.children || [])) { if (c.textContent) o.push(c.textContent); o = o.concat(__texts(c)); } return o; };
            """);
        // an NFT with on-chain metadata but no on-chain art still opens a lightbox with name, description, and traits
        object result = engine.Evaluate("""
            (function () {
                const thumb = { id: '7', src: null, metaFetched: true, name: 'Mouse #7', description: 'a mouse',
                    attributes: [{ trait_type: 'hat', value: 'No Hat' }, { trait_type: 'neck', value: 'Plain' }] };
                const collection = { address: '0x000000000000000000000000000000000000dEaD', ticker: 'MICE', name: 'Anonymice' };
                openArt(thumb, collection, { meta: {} });
                const overlay = document.body.children[document.body.children.length - 1];
                const flat = __texts(overlay);
                return JSON.stringify({
                    noArt: flat.includes('no on-chain image'),
                    title: flat.includes('Mouse #7'),
                    desc: flat.includes('a mouse'),
                    traitParts: flat.filter((t) => ['hat', 'No Hat', 'neck', 'Plain'].includes(t)).length,
                    copyAddr: flat.some((t) => t.includes('0x0000') && t.includes('dEaD') && !t.includes('↗')),
                });
            })()
            """);
        Assert.That(result, Is.EqualTo("{\"noArt\":true,\"title\":true,\"desc\":true,\"traitParts\":4,\"copyAddr\":true}"));
    }

    [Test]
    public void OpenArt_DoesNotEchoNameInSubline()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute(DomShim + """
            globalThis.document.body = document.createElement('body');
            globalThis.document.addEventListener = () => {};
            globalThis.document.removeEventListener = () => {};
            globalThis.__texts = (n) => { let o = []; for (const c of (n.children || [])) { if (c.textContent) o.push(c.textContent); o = o.concat(__texts(c)); } return o; };
            """);
        // when the metadata name already carries the collection + id ("Anonymice #16"), the subline
        // must not repeat it as "Anonymice · #16"; a bare name ("Stuart") still gets the context line
        object result = engine.Evaluate("""
            (function () {
                const collection = { address: '0x00000000000000000000000000000000000000AA', ticker: 'MICE', name: 'Anonymice' };
                const node = { meta: {} };
                function subOf(name) {
                    document.body.children.length = 0;
                    openArt({ id: '16', src: null, metaFetched: true, name }, collection, node);
                    return __texts(document.body.children[0]);
                }
                const full = subOf('Anonymice #16');
                const bare = subOf('Stuart');
                return JSON.stringify({
                    fullHasName: full.includes('Anonymice #16'),
                    fullHasRedundantSub: full.includes('Anonymice · #16'),
                    bareHasSub: bare.includes('Anonymice · #16'),
                });
            })()
            """);
        Assert.That(result, Is.EqualTo("{\"fullHasName\":true,\"fullHasRedundantSub\":false,\"bareHasSub\":true}"));
    }

    [Test]
    public void ExceedsPoolLiquidity_FlagsHoldingsBiggerThanTheExitPool()
    {
        using V8ScriptEngine engine = CreateEngine();
        // a DEX-priced holding worth more than the pool's exit liquidity is a spoof (VITALIK-style airdrop +
        // shallow manipulated pool); one within the pool's liquidity is fine; a token with no pool entry
        // (Chainlink-priced) is never flagged
        object result = engine.Evaluate("""
            (function () {
                const usd = (n) => BigInt(n) * (10n ** 8n);
                dexLiquidity8.set('0xspoof', usd(7268));
                dexLiquidity8.set('0xthin', usd(8000));
                return JSON.stringify({
                    spoof: exceedsPoolLiquidity('0xspoof', usd(192428)),
                    within: exceedsPoolLiquidity('0xthin', usd(5000)),
                    noPool: exceedsPoolLiquidity('0xchainlink', usd(1000000)),
                });
            })()
            """);
        Assert.That(result, Is.EqualTo("{\"spoof\":true,\"within\":false,\"noPool\":false}"));
    }

    [Test]
    public void ResolveArtUri_RoutesByEnabledSourcesAndAlwaysAllowsOnChain()
    {
        using V8ScriptEngine engine = CreateEngine();
        object result = engine.Evaluate("""
            (function () {
                const R = (u) => String(resolveArtUri(u) ?? '');
                artSources.https = false; artSources.localIpfs = false; artSources.remoteGateway = false;
                const allOff = { data: R('data:image/svg+xml,x'), ipfs: R('ipfs://QmABC/1'), https: R('https://x/y') };
                artSources.localIpfs = true;
                const local = R('ipfs://ipfs/QmABC');
                artSources.localIpfs = false; artSources.remoteGateway = true;
                const remote = R('https://gw/ipfs/QmXYZ/2'); // gateway-style ipfs
                artSources.https = true;
                const http = R('https://example.com/api/1');
                ipfsRemoteGateway = 'https://dweb.link/ipfs/'; // configurable gateway
                const customGw = R('ipfs://QmABC');
                return JSON.stringify({ onchainAlways: allOff.data, ipfsOff: allOff.ipfs, httpsOff: allOff.https, local, remote, http, customGw });
            })()
            """);
        Assert.That(result, Is.EqualTo(
            "{\"onchainAlways\":\"data:image/svg+xml,x\",\"ipfsOff\":\"\",\"httpsOff\":\"\","
          + "\"local\":\"portfolio-ipfs/QmABC\",\"remote\":\"https://ipfs.io/ipfs/QmXYZ/2\",\"http\":\"https://example.com/api/1\","
          + "\"customGw\":\"https://dweb.link/ipfs/QmABC\"}"));
    }

    [Test]
    public void Chains_IncludeOptimism()
    {
        using V8ScriptEngine engine = CreateEngine();
        object result = engine.Evaluate(
            "JSON.stringify({ name: CHAINS[10].name, v3: !!CHAINS[10].dex.v3, quotes: CHAINS[10].dex.quotes.length, nativeFeed: !!CHAINS[10].feeds.native })");
        Assert.That(result, Is.EqualTo("{\"name\":\"Optimism\",\"v3\":true,\"quotes\":2,\"nativeFeed\":true}"));
    }

    [TestCase("ipfs://cid/clip.mp4", "video")]
    [TestCase("https://x/a.webm?ext=1", "video")]
    [TestCase("https://x/song.mp3", "audio")]
    [TestCase("https://x/model.glb", "")]
    [TestCase("https://x/pic.png", "")]
    public void MediaType_ClassifiesVideoAudioByExtension(string url, string expected)
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.That(engine.Evaluate($"String(mediaType('{url}') ?? '')"), Is.EqualTo(expected));
    }

    [Test]
    public void MediaType_UsesFormatHintForExtensionlessUrls()
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate("String(mediaType('https://arweave.net/abc', 'MP4') ?? '')"), Is.EqualTo("video"), "arweave mp4 via format");
            Assert.That(engine.Evaluate("String(mediaType('ipfs://cid', 'WAV') ?? '')"), Is.EqualTo("audio"), "ipfs wav via format");
            Assert.That(engine.Evaluate("String(mediaType('https://arweave.net/abc', 'GLB') ?? '')"), Is.EqualTo(""), "glb ignored");
            Assert.That(engine.Evaluate("String(mediaType('https://arweave.net/abc') ?? '')"), Is.EqualTo(""), "no ext, no hint");
        });
    }

    [Test]
    public void OpenArt_RendersVideoAndAudioMedia()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute(DomShim + """
            globalThis.document.body = document.createElement('body');
            globalThis.document.addEventListener = () => {};
            globalThis.document.removeEventListener = () => {};
            """);
        // video replaces the still image; audio plays alongside the cover image
        object result = engine.Evaluate("""
            (function () {
                const coll = { address: '0x00000000000000000000000000000000000000AA', ticker: 'X', name: 'X' };
                const tags = (p) => (p.children || []).map((c) => c.tag);
                const open = (thumb) => { document.body.children.length = 0; openArt(thumb, coll, { meta: {} }); return document.body.children[0].children[0]; };
                const v = tags(open({ id: '1', src: 'data:image/png,x', anim: 'https://x/v.mp4', animType: 'video', name: 'V' }));
                const a = tags(open({ id: '2', src: 'data:image/png,x', anim: 'https://x/s.mp3', animType: 'audio', name: 'A' }));
                return JSON.stringify({ video: v.includes('video'), videoReplacesImg: !v.includes('img'), audio: a.includes('audio'), audioKeepsImg: a.includes('img') });
            })()
            """);
        Assert.That(result, Is.EqualTo("{\"video\":true,\"videoReplacesImg\":true,\"audio\":true,\"audioKeepsImg\":true}"));
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

    // token-info lightbox per-unit price: 2dp at/above 1.00, extra precision below so cheap tokens aren't "0.00"
    [TestCase("150000000", "$1.50")]
    [TestCase("250000000", "$2.50")]
    [TestCase("5000000", "$0.0500")]
    [TestCase("12300", "$0.000123")]
    [TestCase("0", "$0")]
    public void FormatPrice8_KeepsSubDollarPrecision(string cur8, string expected)
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.That(engine.Evaluate($"formatPrice8({cur8}n)"), Is.EqualTo(expected));
    }

    [Test]
    public void FormatPrice8_ReturnsDashForMissingPrice()
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.That(engine.Evaluate("formatPrice8(null)"), Is.EqualTo("—"));
    }

    // market cap uses compact suffixes; sub-thousand values fall through to the plain fiat format
    [TestCase("123000000000000000", "$1.23B")]
    [TestCase("250000000000000", "$2.50M")]
    [TestCase("50000000000", "$500.00")]
    public void FormatFiatCompact_AbbreviatesLargeAggregates(string cur8, string expected)
    {
        using V8ScriptEngine engine = CreateEngine();
        Assert.That(engine.Evaluate($"formatFiatCompact({cur8}n)"), Is.EqualTo(expected));
    }

    // Regression: re-enabling "Hide spam" must re-hide the spam-flagged auto-detected tokens/collections
    // (that turning it off restored) while keeping genuine ones. Previously nothing re-hid them.
    [Test]
    public void HideSpamNow_ReHidesSpamFlaggedAndKeepsGenuine()
    {
        using V8ScriptEngine engine = CreateEngine();
        engine.Execute("""
            nodes = [{ chainId: 1 }];
            states.clear();
            states.set(1, {
                detected: [
                    { address: '0xAA', ticker: 'USDC', decimals: 6, spam: false },
                    { address: '0xBB', ticker: 'SPAM', decimals: 18, spam: true }
                ],
                nfts: [
                    { address: '0xCC', ticker: 'REAL', name: 'Real', auto: true, spam: false },
                    { address: '0xDD', ticker: 'SCAM', name: 'Scam', auto: true, spam: true }
                ],
                ignored: [], tokens: []
            });
            store.save = () => {};
            renderTokens = () => {}; renderCards = () => {}; recomputeTotals = () => {};
            """);
        object result = engine.Evaluate("""
            (function () {
                hideSpamNow();
                const s = states.get(1);
                return JSON.stringify({
                    detected: s.detected.map(t => t.ticker),
                    nfts: s.nfts.map(t => t.ticker),
                    ignored: s.ignored.map(t => t.ticker)
                });
            })()
            """);
        Assert.That(result, Is.EqualTo("{\"detected\":[\"USDC\"],\"nfts\":[\"REAL\"],\"ignored\":[\"SPAM\",\"SCAM\"]}"));
    }
}
