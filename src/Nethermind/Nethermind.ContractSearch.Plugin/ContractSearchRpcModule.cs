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
        try
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
                new ContractBytecodeSearchVisitor(bytecodes, results, _worldStateManager.GlobalStateReader),
                head.StateRoot);

            return ResultWrapper<ContractSearchResult[]>.Success([.. results]);
        }
        catch (Exception ex)
        {
            _logger.Error("Error during bytecode search", ex);
            return ResultWrapper<ContractSearchResult[]>.Fail(ex.Message);
        }
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
    IStateReader stateReader) : ITreeVisitor
{
    private readonly byte[][] _searchBytecodes = searchBytecodes;
    private readonly List<ContractSearchResult> _results = results;
    private readonly IStateReader _stateReader = stateReader;
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
        byte[] code = _stateReader.GetCode(codeHash)!;
        var matchIndices = new List<int[]>();

        foreach (var searchCode in _searchBytecodes)
        {
            var indices = FindAllIndices(code, searchCode);
            if (indices.Length > 0)
            {
                matchIndices.Add(indices);
            }
        }

        if (matchIndices.Count == _searchBytecodes.Length)
        {
            _results.Add(new ContractSearchResult
            {
                Address = _currentAddress,
                MatchIndices = [.. matchIndices]
            });
        }
    }

    private static int[] FindAllIndices(byte[] source, byte[] pattern)
    {
        var indices = new List<int>();
        int currentIndex = 0;
        ReadOnlySpan<byte> sourceSpan = source;

        while (currentIndex <= source.Length - pattern.Length)
        {
            int index = sourceSpan[currentIndex..].IndexOf(pattern);
            if (index == -1)
                break;

            indices.Add(currentIndex + index);
            currentIndex += index + 1;
        }

        return [.. indices];
    }
}
