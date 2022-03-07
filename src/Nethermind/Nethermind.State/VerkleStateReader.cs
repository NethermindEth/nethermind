using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Metrics = Nethermind.Db.Metrics;


namespace Nethermind.State;

public class VerkleStateReader: IStateReader
{
    private readonly IDb _codeDb;
    private readonly ILogger _logger;
    private readonly VerkleStateTree _state;

    public VerkleStateReader(IVerkleTrieStore verkleTrieStore, IDb? codeDb, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<StateReader>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _state = new VerkleStateTree(verkleTrieStore, logManager);
    }

    public Account? GetAccount(Keccak stateRoot, Address address)
    {
        return GetState(stateRoot, address);
    }

    public byte[] GetStorage(Address address, in UInt256 index)
    {
        Metrics.StorageTreeReads++;
        return _state.GetStorageValue(new StorageCell(address, index));
    }

    public byte[] GetStorage(Keccak storageRoot, in UInt256 index)
    {
        if (storageRoot != Keccak.Zero)
        {
            throw new InvalidOperationException("verkle tree does not support storage root");
        }

        return new byte[32];

    }

    public UInt256 GetBalance(Keccak stateRoot, Address address)
    {
        return GetState(stateRoot, address)?.Balance ?? UInt256.Zero;
    }

    public byte[]? GetCode(Keccak codeHash)
    {
        if (codeHash == Keccak.OfAnEmptyString)
        {
            return Array.Empty<byte>();
        }

        return _codeDb[codeHash.Bytes];
    }

    public byte[] GetCode(Keccak stateRoot, Address address)
    {
        Account? account = GetState(stateRoot, address);
        return account is null ? Array.Empty<byte>() : GetCode(account.CodeHash);
    }

    public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
    {
        _state.Accept(treeVisitor, rootHash, visitingOptions);
    }


    private Account? GetState(Keccak stateRoot, Address address)
    {
        if (stateRoot == Keccak.EmptyTreeHash)
        {
            return null;
        }

        Metrics.StateTreeReads++;
        Account? account = _state.Get(address, stateRoot);
        return account;
    }

}
