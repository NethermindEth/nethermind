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

        // SpecProviderBase.LoadTransitions rejects a chainspec whose transitions are ordered so that a
        // block-number transition can never activate (#13202). That turns a silent misconfiguration into a refusal
        // to start, so every chainspec we ship has to be proven to survive it - not just the handful the
        // Nethermind.Specs.Test fixture names one by one. This project is the only test project that references
        // Nethermind.Runner, so it is the only place where the engine parameters of the plugin chains (Taiko,
        // Linea, JOC, Surge) resolve and their chainspecs load at all.
        [TestCaseSource(nameof(ShippedChainSpecs))]
        public void Every_shipped_chainspec_builds_a_spec_provider(string chainSpecFile)
        {
            ChainSpec chainSpec = _loader.LoadEmbeddedOrFromFile(Path.Combine("chainspec", chainSpecFile));

            Assert.That(() => new ChainSpecBasedSpecProvider(chainSpec, LimboLogs.Instance), Throws.Nothing);
        }

        private static string[] ShippedChainSpecs()
        {
            string folder = Path.Combine(TestContext.CurrentContext.TestDirectory, "chainspec");
            List<string> files = [];
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(path);
                if (name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".json.zst", StringComparison.Ordinal))
                {
                    files.Add(name);
                }
            }

            files.Sort(StringComparer.Ordinal);

            // An empty source is reported as a pass, which is the one outcome this must not have.
            Assert.That(files, Is.Not.Empty, $"no chainspecs found under {folder}");
            return files.ToArray();
        }
    }
}
