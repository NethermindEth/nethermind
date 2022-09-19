//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class SnapServer
{
    private readonly ITrieStore _store;
    private readonly IDbProvider _dbProvider;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    private readonly AccountDecoder _decoder = new();

    public SnapServer(IDbProvider dbProvider, ILogManager logManager)
    {
        _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
        _store = new TrieStore(
            _dbProvider.StateDb,
            logManager);

        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = logManager.GetClassLogger();
    }


    public byte[][]?  GetTrieNodes(byte[][][] pathSet, Keccak rootHash)
    {
        int pathLength = pathSet.Length;
        List<byte[]> response = new ();
        StateTree tree = new(_store, _logManager);

        for (int reqi = 0; reqi < pathLength; reqi++)
        {
            var requestedPath = pathSet[reqi];
            switch (requestedPath.Length)
            {
                case 0:
                    return null;
                case 1:
                    var rlp = tree.Get(requestedPath[0], rootHash);
                    response.Add(rlp);
                    break;
                default:
                    byte[]? accBytes = tree.Get(requestedPath[0], rootHash);
                    if (accBytes is null)
                    {
                        // TODO: how to deal with empty account when storage asked?
                        response.Add(null);
                        continue;
                    }
                    Account? account = _decoder.Decode(accBytes.AsRlpStream());
                    var storageRoot = account.StorageRoot;
                    StorageTree sTree = new StorageTree(_store, storageRoot, _logManager);

                    for (int reqStorage = 1; reqStorage < requestedPath.Length; reqStorage++)
                    {
                        var sRlp = sTree.Get(requestedPath[reqStorage]);
                        response.Add(sRlp);
                    }
                    break;
            }
        }

        return response.ToArray();
    }

    public byte[][] GetByteCodes(Keccak[] requestedHashes)
    {
        List<byte[]> response = new ();

        for (int codeHashIndex = 0; codeHashIndex < requestedHashes.Length; codeHashIndex++)
        {
            response.Add(_dbProvider.CodeDb.Get(requestedHashes[codeHashIndex]));
        }

        return response.ToArray();
    }

}
