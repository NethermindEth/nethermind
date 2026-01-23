// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Go through the trie and check for the corresponding value in flat.
/// </summary>
public class FlatVerifyTrieVisitor : TrieStatsCollector
{
    private readonly IPersistence.IPersistenceReader _persistenceReader;
    private long _mismatchedAccount;
    private long _mismatchedSlot;

    public FlatVerifyTrieVisitor(
        IKeyValueStore codeKeyValueStore,
        IPersistence.IPersistenceReader persistenceReader,
        ILogManager logManager,
        CancellationToken cancellationToken = default)
        : base(codeKeyValueStore, logManager, "Trie->Flat Verify", cancellationToken, expectAccounts: true)
    {
        _persistenceReader = persistenceReader;
    }

    public long MismatchedAccount => _mismatchedAccount;
    public long MismatchedSlot => _mismatchedSlot;

    public override void VisitLeaf(in Context nodeContext, TrieNode node)
    {
        // Let base class handle stats updates
        base.VisitLeaf(nodeContext, node);

        // Add flat verification logic
        if (nodeContext.IsStorage)
        {
            Hash256 fullPath = nodeContext.Path.Append(node.Key).Path.ToHash256();
            byte[]? nodeSlot = State.StorageTree.ZeroBytes;
            if (node.Value.IsNotNull)
            {
                Rlp.ValueDecoderContext ctx = node.Value.Span.AsRlpValueContext();
                nodeSlot = ctx.DecodeByteArray();
            }

            byte[]? flatSlot = _persistenceReader.GetStorageRaw(nodeContext.Storage!, fullPath);
            if (!Bytes.AreEqual(flatSlot, nodeSlot))
            {
                if (_logger.IsWarn) _logger.Warn($"Mismatched slot. AddressHash: {nodeContext.Storage}. SlotHash {fullPath}. Trie slot: {nodeSlot.ToHexString() ?? ""}, Flat slot; {flatSlot?.ToHexString()}");
                Interlocked.Increment(ref _mismatchedSlot);
            }
        }
        else
        {
            Hash256 addrHash = nodeContext.Path.Append(node.Key).Path.ToHash256();
            byte[]? rawAccountBytes = _persistenceReader.GetAccountRaw(addrHash);
            Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(rawAccountBytes);
            Account? flatAccount = AccountDecoder.Instance.Decode(ref ctx);

            ctx = node.Value.Span.AsRlpValueContext();
            Account? nodeAccount = AccountDecoder.Instance.Decode(ref ctx);

            if (nodeAccount != flatAccount)
            {
                if (_logger.IsWarn) _logger.Warn($"Mismatched account. AddressHash: {addrHash}. Trie account: {nodeAccount}, Flat account; {flatAccount}");
                Interlocked.Increment(ref _mismatchedAccount);
            }
        }
    }
}
