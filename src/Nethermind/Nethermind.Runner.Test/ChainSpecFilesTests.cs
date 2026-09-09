// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Logging;
using NUnit.Framework;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ChainSpecFilesTests
    {
        private readonly ChainSpecFileLoader _loader;

        public ChainSpecFilesTests() => _loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance);

        [TestCase("foundation", 1UL)]
        [TestCase("chainspec/foundation", 1UL)]
        [TestCase("chainspec/foundation.json", 1UL)]
        public void different_formats_to_chainSpecPath(string chainSpecPath, ulong chainId) =>
            Assert.That(_loader.LoadEmbeddedOrFromFile(chainSpecPath).ChainId, Is.EqualTo(chainId));

        [TestCase("testspec.json", 0x55UL)]
        public void ChainSpec_from_file(string chainSpecPath, ulong chainId) =>
            Assert.That(_loader.LoadEmbeddedOrFromFile(chainSpecPath).ChainId, Is.EqualTo(chainId));

        [TestCase("chainspec/custom_chainspec_that_does_not_exist.json")]
        public void ChainSpecNotFound(string chainSpecPath)
        {
            Func<ChainSpec> tryLoad = () => _loader.LoadEmbeddedOrFromFile(chainSpecPath);
            Assert.That(tryLoad, Throws.TypeOf<FileNotFoundException>());
        }

        [TestCase("chainspec/op-mainnet.json.zst", 10UL)]
        public void Zstandard_Compressed_ChainSpec(string chainSpecPath, ulong chainId) =>
            Assert.That(_loader.LoadEmbeddedOrFromFile(chainSpecPath).ChainId, Is.EqualTo(chainId));

        // SpecProviderBase.LoadTransitions refuses a transition list in which a block-number transition could
        // never activate (#13202), so every shipped chainspec has to be proven to construct a provider - not just
        // the handful Nethermind.Specs.Test names one by one. It lives here because TypeDiscovery resolves plugin
        // types through the reference closure - loaded assemblies plus what they reference - and referencing
        // Nethermind.Runner is what pulls every plugin assembly into it. Without that reference the plugin chains'
        // engine parameters do not resolve at all and Taiko, Linea, JOC and Surge fail with "No seal engine in
        // chain spec".
        [TestCaseSource(nameof(ShippedChainSpecs))]
        public void Every_shipped_chainspec_builds_a_spec_provider(string chainSpecPath)
        {
            ChainSpec chainSpec = _loader.LoadEmbeddedOrFromFile(chainSpecPath);

            Assert.That(() => new ChainSpecBasedSpecProvider(chainSpec, LimboLogs.Instance), Throws.Nothing);
        }

        private static string[] ShippedChainSpecs()
        {
            string folder = Path.Combine(TestContext.CurrentContext.TestDirectory, "chainspec");
            List<string> paths = [];
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (path.EndsWith(".json", StringComparison.Ordinal) || path.EndsWith(".json.zst", StringComparison.Ordinal))
                {
                    paths.Add(path);
                }
            }

            paths.Sort(StringComparer.Ordinal);

            // An empty source is reported as a pass, which is the one outcome this must not have. Thrown rather
            // than asserted: this runs at discovery time, where an assertion failure is a fixture load error.
            if (paths.Count == 0) throw new InvalidOperationException($"no chainspecs found under {folder}");

            return paths.ToArray();
        }
    }
}
