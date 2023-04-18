// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Trie
{
    public class TrieStatsCollector : ITreeVisitor
    {
        private readonly IKeyValueStore _codeKeyValueStore;
        private int _lastAccountNodeCount = 0;

        private readonly ILogger _logger;

        private HashSet<string> missingNodes = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        private string[] Missing =
        {
            "0xd16e28c81e476d0683472ce07358d76e11c67251fd579445d21e4624f0f63076", "0x6ada84848a6fbbcb203b05d340b05d1123cbf313fe843be3b25d71ef8359026c", "0x47d41b1fdd75fb7be3175acc12613ab49538c9dfe61aa4f1eb05264c22d573bd",
            "0x0a2e110022f01f015ac62e9152b87c66047ccb5b42b3e92391e249573575ccbf", "0x6bbaa09911a1313e3448c1067e2547302c61f873ffa9edbd02e1bff2b3525275", "0x9b94d26aaae30765cb3bf7f5d1755b91ff59512c3fa4c9db5838448e40e37415",
            "0xb519a933a506df1f058bcd73e2e0d67b4ceff49d24873f52961167f45b2d1f04", "0xe8f77cfdc550a5b17892d7e3acf3d2e3c4a7657a81aa7a19f4175382101376b5", "0x710611e551559ebd54d6f48e0cc37748f4013ee0d7bd1d398f6f4310ac29daa3",
            "0x737df89f3b8468698eb5b7ae1236e19f9294a6ddf2d57b64789770a829a06623", "0x7b0f7cf213610ed83bb32cafa0d32b989186c7a84e576758fcae58fa90ede29a", "0x77e4341587853a4a3450291e0ea204f917d98aef35775d6e63276e4d5ef899d7",
            "0x8d820d201fcf4844a2264b168369b813965614ff26d1885e559043c57baf0c34", "0x14586e18abc6465299e7dafc454eb2802de56b5b48c5babb111a1c7cc85dc3a0", "0x41a8265632b6667e35577c5b251598fc3b5bc972efeec1c2a5af64c246dc3a15",
            "0x06dfde366ef399d52315a0dad55588aad3b71d4c8e00d9a799b71973326de47a", "0xefd3760b8c149dc72f41a52482ad7f0601da2a8817afff3a4503b3dc3f4fe86a", "0xc07cf3dbe9ecb6605a2b6908ab18e80700400a757da6fe6f0674574c6fe86f6b",
            "0x393019d577cdfa20c2a7952044912dbb14d793ae0158f139c56e602bdc1e7058", "0x89555ebb171b91437b170501257fd3b91dd56af973622218180eea466a708f83", "0x6719eb8a09226695a1036790f8ea34dea36fedb93578adc065ba396cd6934823",
            "0x067f38f811607c2695d4c302eca61f7c08cd3c610443bb3b27434b1a6778b1a2", "0xaab33e228d386b58a8e3adad9a22c76e783f6ca8704b51c5a18c83cf87145e64", "0x4fc5f13ab2f9ba0c2da88b0151ab0e7cf4d85d08cca45ccd923c6ab76323eb28",
            "0xf6bf00a61a929d2ed7b3730d30ddf9bd58c3ff5827a87fb0bfe4ebdda4739fe1", "0x14c9199f82bdebb035e39123842c49b868622d558eb7bffc58f8345129f2f725", "0xa7f1394d677afecc5364abe26b39abae24cbff2ba8052381b7a808306ea30a3f",
            "0x8f367d2d8609ae542dd6035abda004490479f5bdd48431d7c85a9edc4294973f", "0x7fff462e8680958354993661cbacb5a794ee83778473d56f6defe9a3111747c0", "0xf4984a11f61a2921456141df88de6e1a710d28681b91af794c5a721e47839cd7",
            "0x8bf408b04b8460bb97ad4a52eef0900bb6d89bfbd79bc6fdc7ffe2e12eed360e", "0x5f92d2d6b6816dbd2879b63f118474ad71e0de3499d57151c17e4d371db2fff7", "0x9d44c7b936597b11c42037fe4c8d875437f4905b2ddb5f24275c0973a261999b",
            "0x1f84736fcab94aa10fbaa415ec6a81de2d045791dbbe0999497cdd4262fc744f", "0x4d75d3a07f5aec9c7afe47cc7fda2922414a1977207dc7fb7732c7cfc34295db", "0x0553328ce8299a1e5e891090bcb7471e785a95aac0f8941c89ad03d10ba55a83",
            "0xde6adabfef1b47d7a717991b6521f610f1211be8496d421d5525bb1ec0ce85ab", "0x2fa356f640d00d73b9ee0d1695ae2b8fe347c85fdaa01739aef446d52a9b6d3c", "0xe5dc160f36a584c33ce3fb55c6971b0dca3ef424d394e9c221d9aaa8bdb62994",
            "0x2d37a659965bacc19bc32ed1dbe86c5f0649d5a18fa0bdd280b7925fb8680b1a", "0x4e8f018a64f723bf2e2a684116c2e7b9c4115a519471d336c355e0410cc45711", "0x0a881d07ae1aebf10b47155b4fa013c97f879ba51e4a808a994d6d51f938c92e",
            "0x4fc5f13ab2f9ba0c2da88b0151ab0e7cf4d85d08cca45ccd923c6ab76323eb28", "0xeddb6495bbfdad1fc42dd0017aa9fd8e91bf9122ae54b4b8bb1b9354e0222aad", "0xec7e50d0aab647bd54423ab682a54cca99ff690bdaa6c036054e325849485796",
            "0x9c258383c3f9dff8795f422aaf4607dd2a6e531ea989c09febdf54d3ddfd48ea", "0x6a54b3b8c58cad45839df74e9cc88538193c7b5c81feb9d8bd8078faa0f478bf", "0x62bb491b8e8b8e88965140d62414065ab38919be589bb19beb126698cd8c4b67",
            "0xcd457259696115235e64c7822334d62129e2f1604425a7da6494f35fc45be518", "0xd425eb8d2e2b548fca23063bafd80083f058667435cff74df1c266f6faa20c3d", "0xed0ed441dea39a5e6d33934ba9196c10e583a59d1e9edfeaf66d826c5886cd89",
            "0x4fc5f13ab2f9ba0c2da88b0151ab0e7cf4d85d08cca45ccd923c6ab76323eb28", "0x8bb230b6917f2c3ee9cbde752bd79e450081da36ed8f39c4fa29628d2ff1e7a9", "0xc1c7713f29db7f8887baeb2ca1c65cca750d778ed5fea608831474e39e736669",
            "0x4f073081496508e1119102469ab1ea833b19cec9ad344fa397193a01d9f366ff", "0x4fc5f13ab2f9ba0c2da88b0151ab0e7cf4d85d08cca45ccd923c6ab76323eb28", "0xe9076c443adeca374e740cf83dabe698ce55a200c14783bbeffb188487ec9a4f",
            "0x90d0713623f0ff5db4e292119b91f104456736200a14a1b0153a7a43df8a47e8", "0xf35ffafb2653653134245cb801f726554b0ed6991a44a8f1e24d135fc52d8fed", "0x4fc5f13ab2f9ba0c2da88b0151ab0e7cf4d85d08cca45ccd923c6ab76323eb28",
            "0x882c1806d7faca5060ea17d2d0c590bc51dd8c88ae233fc23e57dd621ebf0f30", "0xc8e2d6fff149f8268e83f814658e0544705d22b18831bee8ab0f39730d3c988b", "0x6752b158751173491003d12e57849a0ecb8e7979596069cc3098f9569d2ab7ff",
            "0x66a16f3368c574ded629c76f59a0633f0bc5cc3988bf97ecc73598d3902da47e", "0xfae9bc5857ff04c00314b1c84fb02542cb44df3b41d2d12a9d7e2797bca14628", "0x58c1b22ef5836c988fffd1a7344293b6451f47bcbea37c71555bc0fa903b27ad",
            "0x3067cc61dbb7f775c1c9c3aadbe4aa60c1d2778143142b575cd5f4ec8a17bb54", "0x0bdf2778817571f0b837843c8035e21376b19cee1533873593f8a83996b1d709", "0x815e15ceb1a6710894c9d7943aa8c2a4a0bc0276ea167ad3aa961acb203341af",
            "0x3c34b16683d6ea4e5eaa781716de1bbf9b2ebf2ce7a6cb52c5e559d26a542985", "0xebcf9e2ed0b639526545daa920533e3e5b3747a7a66ffea686aee22e16038577", "0x4fc5f13ab2f9ba0c2da88b0151ab0e7cf4d85d08cca45ccd923c6ab76323eb28",
            "0x1bc997525fa4406e51f0dfeef903374342760b574c6897017305b1745eefa430", "0xaf8cec31f98ef592e14f16939f948b679a7cc0f3a0675745d0fc375a30b1b094", "0x038ce3682deb5b23d490fab08d9711e0a7179f357dc864780d05418cdb08d2b4",
        };


        public TrieStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager)
        {
            _codeKeyValueStore = codeKeyValueStore ?? throw new ArgumentNullException(nameof(codeKeyValueStore));
            _logger = logManager.GetClassLogger();
            foreach (string node in Missing)
            {
                missingNodes.Add(node);
            }
        }

        public TrieStats Stats { get; } = new();

        public bool ShouldVisit(Keccak nextNode)
        {
            if (nextNode is not null && missingNodes.Contains(nextNode.Bytes.ToHexString()))
            {
                _logger.Info($"Missing Nodes Encountered: {nextNode}");
            }
            return true;
        }

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
            _logger.Info($"Visiting Tree: {rootHash}");
        }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Increment(ref Stats._missingStorage);
                _logger.Info($"STORAGE MISSING: {nodeHash}");
            }
            else
            {
                Interlocked.Increment(ref Stats._missingState);
                _logger.Info($"STATE MISSING: {nodeHash}");
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._storageBranchCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._stateBranchCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._storageExtensionCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._stateExtensionCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
        {
            if (Stats.NodesCount - _lastAccountNodeCount > 1_000_000)
            {
                _lastAccountNodeCount = Stats.NodesCount;
                _logger.Warn($"Collected info from {Stats.NodesCount} nodes. Missing CODE {Stats.MissingCode} STATE {Stats.MissingState} STORAGE {Stats.MissingStorage}");
            }

            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._storageLeafCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._accountCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
            byte[] code = _codeKeyValueStore[codeHash.Bytes];
            if (code is not null)
            {
                Interlocked.Add(ref Stats._codeSize, code.Length);
                Interlocked.Increment(ref Stats._codeCount);
            }
            else
            {
                Interlocked.Increment(ref Stats._missingCode);
            }

            IncrementLevel(trieVisitContext, Stats._codeLevels);
        }

        private void IncrementLevel(TrieVisitContext trieVisitContext)
        {
            int[] levels = trieVisitContext.IsStorage ? Stats._storageLevels : Stats._stateLevels;
            IncrementLevel(trieVisitContext, levels);
        }

        private static void IncrementLevel(TrieVisitContext trieVisitContext, int[] levels)
        {
            Interlocked.Increment(ref levels[trieVisitContext.Level]);
        }
    }
}
