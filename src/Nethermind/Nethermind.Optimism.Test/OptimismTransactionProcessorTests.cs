// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismTransactionProcessorTests
{
    private MemDb _stateDb;
    private WorldState _worldState;
    private CodeInfoRepository _code;
    private ISpecProvider _spec;
    private VirtualMachine _machine;
    private IDb _codeDb;
    private OptimismSpecHelper _optimismSpec;
    private OPL1CostHelper _l1CostHelper;
    private OptimismTransactionProcessor _processor;

    private static readonly Address L1Address = Address.FromNumber(53455325324534);

    private const ulong HoloceneTimestamp = 900;
    private const ulong IsthmusTimestamp = 1_000;

    [SetUp]
    public virtual void Setup()
    {
        _spec = NSubstitute.Substitute.For<ISpecProvider>();
        _codeDb = new MemDb();
        _stateDb = new MemDb();
        ITrieStore trieStore = new TrieStore(_stateDb, LimboLogs.Instance);
        _worldState = new WorldState(trieStore, _codeDb, LimboLogs.Instance);
        _code = new CodeInfoRepository();
        _machine = new VirtualMachine(NSubstitute.Substitute.For<IBlockhashProvider>(), _spec, _code,
            LimboLogs.Instance);

        _optimismSpec = new OptimismSpecHelper(new OptimismChainSpecEngineParameters
        {
            HoloceneTimestamp = HoloceneTimestamp,
            IsthmusTimestamp = IsthmusTimestamp,
        });

        _l1CostHelper = new OPL1CostHelper(_optimismSpec, L1Address);

        _processor = new OptimismTransactionProcessor(_spec, _worldState, _machine, LimboLogs.Instance, _l1CostHelper, _optimismSpec, _code);
    }

    // [Test]
    // public void Ishtmus()
    // {
    //
    // }
}
