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
            new ContractBytecodeSearchVisitor(bytecodes, results, _worldStateManager.GlobalStateReader, _logger),
            head.StateRoot);

        return ResultWrapper<ContractSearchResult[]>.Success([.. results]);
    }
}

public struct ContractSearchResult
{
    public Address Address { get; set; }
    public int[][] MatchIndices { get; set; }
}

public class ContractBytecodeSearchVisitor(
    byte[][] searchBytecodes,
    List<ContractSearchResult> results,
    IStateReader stateReader,
    ILogger logger) : ITreeVisitor
{
    private readonly byte[][] _searchBytecodes = searchBytecodes;
    private readonly List<ContractSearchResult> _results = results;
    private readonly IStateReader _stateReader = stateReader;
    private readonly ILogger _logger = logger;
    private Address _currentAddress = Address.Zero;

    public bool IsFullDbScan => true;

    public bool ShouldVisit(Hash256 nodeHash) => true;

    public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitMissingNode(Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        if (node.Key is null || node.Key.Length < 20)
        {
            Debug.Assert(false, "Found an invalid node key");
            return;
        }

        ReadOnlySpan<byte> key = node.Key.AsSpan();
        _currentAddress = new Address(key[(key.Length - 20)..]);
    }

    public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext)
    {
        _logger.Info($"Searching contract at {_currentAddress}");

        byte[] code = _stateReader.GetCode(codeHash)!;
        var matchIndices = new List<int[]>();

        foreach (byte[] searchCode in _searchBytecodes)
        {
            IEnumerable<int> indices = FindAllIndices(code, searchCode);
            if (indices.Any())
            {
                matchIndices.Add([.. indices]);
            }
        }

        if (matchIndices.Count == _searchBytecodes.Length)
        {
            _logger.Info($"Found matching contract at {_currentAddress}");
            _results.Add(new ContractSearchResult
            {
                Address = _currentAddress,
                MatchIndices = [.. matchIndices]
            });
        }
    }

    private static IEnumerable<int> FindAllIndices(byte[] source, byte[] pattern)
    {
        ReadOnlySpan<byte> sourceSpan = source;
        ReadOnlySpan<byte> patternSpan = pattern;
        int currentIndex;

        while ((currentIndex = sourceSpan.IndexOf(patternSpan)) != -1)
        {
            yield return currentIndex;
            sourceSpan = sourceSpan[(currentIndex + 1)..];
        }
    }
}
