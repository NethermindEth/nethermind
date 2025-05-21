using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using System.Diagnostics;

namespace Nethermind.ContractSearch.Plugin;

public class ContractSearchRpcModule(IBlockTree blockTree, IWorldStateManager worldStateManager, ILogManager logManager) : IContractSearchRpcModule
{
    private readonly IBlockTree _blockTree = blockTree;
    private readonly IWorldStateManager _worldStateManager = worldStateManager;
    private readonly ILogger _logger = logManager.GetClassLogger();
    private OpcodeIndexer _opcodeIndexer = new();

    public ResultWrapper<ContractSearchResult[]> search_contracts(byte[][] bytecodes)
    {
        Block? head = _blockTree.Head;
        if (head is null)
        {
            return ResultWrapper<ContractSearchResult[]>.Fail("Chain head not available.");
        }

        if (head.StateRoot is null)
        {
            return ResultWrapper<ContractSearchResult[]>.Fail("State root not available.");
        }

        List<ContractSearchResult> results = [];

        _worldStateManager.GlobalStateReader.RunTreeVisitor(
            new ContractBytecodeSearchVisitor(bytecodes, _opcodeIndexer, results, _worldStateManager.GlobalStateReader, _logger),
            head.StateRoot);

        return ResultWrapper<ContractSearchResult[]>.Success([.. results]);
    }
}

public struct ContractSearchResult
{
    public Address Address { get; set; }
    public int[] MatchIndices { get; set; }
}

public class ContractBytecodeSearchVisitor(
    byte[][] searchBytecodes,
    OpcodeIndexer opcodeIndexer,
    List<ContractSearchResult> results,
    IStateReader stateReader,
    ILogger logger) : ITreeVisitor<OldStyleTrieVisitContext>
{
    private readonly byte[][] _searchBytecodes = searchBytecodes;
    private readonly List<ContractSearchResult> _results = results;
    private readonly IStateReader _stateReader = stateReader;
    private readonly ILogger _logger = logger;
    private Address _currentAddress = Address.Zero;
    private OpcodeIndexer _opcodeIndexer = opcodeIndexer;

    public bool IsFullDbScan => true;

    public bool ShouldVisit(in OldStyleTrieVisitContext trieVisitContext, in ValueHash256 nodeHash) => true;

    public void VisitTree(in OldStyleTrieVisitContext trieVisitContext, in ValueHash256 rootHash)
    {
    }

    public void VisitMissingNode(in OldStyleTrieVisitContext trieVisitContext, in ValueHash256 nodeHash)
    {
    }

    public void VisitBranch(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
    {
    }

    public void VisitExtension(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
    {
    }

    public void VisitLeaf(in OldStyleTrieVisitContext trieVisitContext, TrieNode node)
    {
        if (node.Key is null || node.Key.Length < 20)
        {
            Debug.Assert(false, "Found an invalid node key");
            return;
        }

        ReadOnlySpan<byte> key = node.Key.AsSpan();
        _currentAddress = new Address(key[(key.Length - 20)..]);
    }

    public void VisitAccount(in OldStyleTrieVisitContext nodeContext, TrieNode node, in AccountStruct account)
    {

        if (_logger.IsInfo) _logger.Info($"Searching contract at {_currentAddress}");

        if (!account.HasCode) return;
        ValueHash256 codeHash = account.CodeHash;

        ReadOnlySpan<byte> code = _stateReader.GetCode(codeHash);

        _opcodeIndexer.Index(codeHash, code);
        List<int> matchIndices = [];

        foreach (ReadOnlySpan<byte> searchCode in _searchBytecodes)
        {

            var match = PatternSearch.SyntacticPatternSearch(codeHash, code, searchCode, _opcodeIndexer);
            if (match.Count == 0) match = PatternSearch.SemanticPatternSearch(code, searchCode);
            if (match.Count > 0)
            {
                if (_logger.IsInfo) _logger.Info($"Found matching contract at {_currentAddress}");
                _results.Add(new ContractSearchResult
                {
                    Address = _currentAddress,
                    MatchIndices = [.. matchIndices]
                });
            }

        }

    }

}
