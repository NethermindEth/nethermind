// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Overseer.Test.Framework;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Overseer.Test
{
    [Explicit]
    public class AuRaTests : TestBuilder
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public async Task One_validator()
        {
            StartAuRaMiner("auraval1", "0xcff9b5a51f50cfddbbd227a273c769164dfe6b6185b56f63e4eb2c545bf5ca38")
                .Wait(5000)
                .Kill("auraval1");

            await ScenarioCompletion;
        }

        [Test]
        public async Task Multiple_validators()
        {
            (string Name, string Address, string PrivateKey)[] validators = new (string Name, string Address, string PrivateKey)[]
            {
                ("auraval11", "0x557abc72a6594d1bd9a655a1cb58a595526416c8", "0xcff9b5a51f50cfddbbd227a273c769164dfe6b6185b56f63e4eb2c545bf5ca38"),
                ("auraval22", "0x69399093be61566a1c86b09bd02612c6bf31214f", "0xcb807c162517bfb179adfeee0d440b81e0bba770e377be4f887e0a4e6c27575d"),
                ("auraval33", "0x4cb87ff61e0e3f9f4043f69fe391a62b5a018b97", "0x2429abae64ce7db0f75941082dc6fa1de10c48a7907f29f54c1c1e9f5bd2baf3"),
            };

            var auRaState = new AuRaState();

            var context =
                StartAuRaMiner(validators[0].Name, validators[0].PrivateKey)
                    .StartAuRaMiner(validators[1].Name, validators[1].PrivateKey)
                    .StartAuRaMiner(validators[2].Name, validators[2].PrivateKey)
                    .SetContext(new AuRaContext(auRaState))
                    .Wait(40000)
                    .SwitchNode(validators[1].Name)
                    .ReadBlockNumber();

            await ScenarioCompletion;

            context.ReadBlockAuthors()
                .LeaveContext()
                .KillAll();

            await ScenarioCompletion;

            var expectedCount = 14;

            auRaState.BlocksCount.Should().BeGreaterOrEqualTo(expectedCount, $"at least {expectedCount} steps.");

            var blockNumbers = auRaState.Blocks.Take(expectedCount).Select(v => v.Key);
            blockNumbers.Should().BeEquivalentTo(Enumerable.Range(1, expectedCount), "block numbers sequential from 1.");

            var steps = auRaState.Blocks.Take(expectedCount).Select(v => v.Value.Step);
            var startStep = auRaState.Blocks.First().Value.Step;
            steps.Should().BeEquivalentTo(Enumerable.Range(0, expectedCount).Select(i => i + startStep), $"steps sequential from {startStep}.");

            var authors = auRaState.Blocks.Take(expectedCount).Select(v => v.Value.Author).Distinct();
            authors.Should().Contain(validators.Select(v => v.Address), "each validator produced a block.");
        }
    }
}
