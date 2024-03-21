// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Data;


/// <summary>
///  Represent an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayloadV3
{
    public ExecutionPayloadV4() { } // Needed for tests

    public ExecutionPayloadV4(Block block) : base(block)
    {
        // SetInclusionList(block?.InclusionList);
    }


    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        if (base.TryGetBlock(out block, totalDifficulty))
        {
            Transaction[]? inclusionList = GetInclusionList();
            if (inclusionList is not null)
            {
                // block!.InclusionList = inclusionList;
                // block!.Header.InclusionListTxRoot = inclusionList is null ? null : TxTrie.CalculateRoot(inclusionList);
            }
            return true;
        }
        return false;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).IsEip7547Enabled;


    private byte[][]? _inclusionList = null;
    private byte[][]? _encodedInclusionList = null;

    /// <summary>
    /// Gets or sets <see cref="Inclusion List"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7547">EIP-7547</see>.
    /// </summary>
    public byte[][]? InclusionList
    {
        get { return _encodedInclusionList; }
        set
        {
            _encodedInclusionList = value;
            _inclusionList = null;
        }
    }

    /// <summary>
    /// Decodes and returns an array of <see cref="Transaction"/> from <see cref="InclusionList"/>.
    /// </summary>
    public Transaction[]? GetInclusionList() => (_inclusionList ??= InclusionList)?.Select((t, i) =>
            {
                try
                {
                    return Rlp.Decode<Transaction>(t, RlpBehaviors.SkipTypedWrapping);
                }
                catch (RlpException e)
                {
                    throw new RlpException($"Transaction {i} is not valid", e);
                }
            }).ToArray();

    /// <summary>
    /// Decodes and returns an array of <see cref="Transaction"/> from <see cref="InclusionList"/>.
    /// </summary>
    /// <Param name="inclusionList">An array of transactions to encode.</Param>
    public void SetInclusionList(params Transaction[]? inclusionList)
    {

        if (inclusionList is null)
        {
            InclusionList = null;
            return;
        }

        InclusionList = inclusionList
            .Select(t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
            .ToArray();
    }
}
