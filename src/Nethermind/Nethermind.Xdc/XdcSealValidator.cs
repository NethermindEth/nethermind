// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Xdc;

internal class XdcSealValidator(
    IMasternodesCalculator masternodesCalculator,
    IEpochSwitchManager epochSwitchManager,
    ISpecProvider specProvider) : ISealValidator
{
    private readonly EthereumEcdsa _ethereumEcdsa = new(0); //Ignore chainId since we don't sign transactions here
    private readonly XdcHeaderDecoder _headerDecoder = new();

    protected IMasternodesCalculator MasternodesCalculator { get; } = masternodesCalculator;
    protected ISpecProvider SpecProvider { get; } = specProvider;

    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false) => ValidateParams(parent, header, out _);

    public virtual bool ValidateParams(BlockHeader parent, BlockHeader header, [NotNullWhen(false)] out string? error)
    {
        if (header is not XdcBlockHeader xdcHeader)
            throw new ArgumentException($"Only type of {nameof(XdcBlockHeader)} is allowed, but got type {header.GetType().Name}.", nameof(header));
        if (xdcHeader.ExtraConsensusData is null)
        {
            error = "ExtraData doesn't contain required consensus data.";
            return false;
        }

        ExtraFieldsV2 extraFieldsV2 = xdcHeader.ExtraConsensusData!;

        if (extraFieldsV2.BlockRound <= extraFieldsV2.QuorumCert.ProposedBlockInfo.Round)
        {
            error = "Round number is not greater than the round in the QC.";
            return false;
        }

        IXdcReleaseSpec xdcSpec = SpecProvider.GetXdcSpec(xdcHeader); // will throw if no spec found

        Address[] masternodes;

        if (epochSwitchManager.IsEpochSwitchAtBlock(xdcHeader))
        {
            if (xdcHeader.Nonce != XdcConstants.NonceDropVoteValue)
            {
                error = "Vote nonce in checkpoint block non-zero.";
                return false;
            }
            (masternodes, Address[] penalties) = MasternodesCalculator.CalculateNextEpochMasternodes(xdcHeader.Number, xdcHeader.ParentHash, xdcSpec);

            if (!ValidateEpochFields(xdcHeader, masternodes, penalties, out error))
                return false;
        }
        else
        {
            if (!ValidateNonEpochFields(xdcHeader, out error))
                return false;
            EpochSwitchInfo epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(xdcHeader);
            masternodes = epochSwitchInfo.Masternodes;
            if (masternodes is null || masternodes.Length == 0)
                throw new InvalidOperationException($"Snap shot returned no master nodes for header \n{xdcHeader}");
        }

        ulong currentLeaderIndex = (xdcHeader.ExtraConsensusData.BlockRound % (ulong)xdcSpec.EpochLength % (ulong)masternodes.Length);
        if (masternodes[(int)currentLeaderIndex] != header.Author)
        {
            error = $"Block proposer {header.Author} is not the current leader.";
            return false;
        }

        error = null;
        return true;
    }

    protected virtual bool ValidateEpochFields(XdcBlockHeader xdcHeader, Address[] masternodes, Address[] penalties, out string? error)
    {
        if (xdcHeader.Validators is null || xdcHeader.Validators.Length == 0)
        {
            error = "Empty validators list on epoch switch block.";
            return false;
        }
        if (xdcHeader.Validators.Length % Address.Size != 0)
        {
            error = "Invalid signer list on checkpoint block.";
            return false;
        }
        if (!xdcHeader.ValidatorsAddress.ListsAreEqual(masternodes))
        {
            error = "Validators does not match what's stored in snapshot minus its penalty.";
            return false;
        }
        if (!xdcHeader.PenaltiesAddress.ListsAreEqual(penalties))
        {
            error = "Penalties does not match.";
            return false;
        }
        error = null;
        return true;
    }

    protected virtual bool ValidateNonEpochFields(XdcBlockHeader xdcHeader, out string? error)
    {
        if (xdcHeader.Validators is not null && xdcHeader.Validators.Length != 0)
        {
            error = "Validators are not empty in non-epoch switch header.";
            return false;
        }
        if (xdcHeader.Penalties is not null &&
            xdcHeader.Penalties.Length != 0)
        {
            error = "Penalties are not empty in non-epoch switch header.";
            return false;
        }
        error = null;
        return true;
    }

    public bool ValidateSeal(BlockHeader header, bool force) => ValidateSeal(header);
    public bool ValidateSeal(BlockHeader header)
    {
        if (header is not XdcBlockHeader xdcHeader)
            throw new ArgumentException($"Only type of {nameof(XdcBlockHeader)} is allowed, but got type {header.GetType().Name}.", nameof(header));
        if (xdcHeader.Number == 0)
            return true;
        if (header.Author is null)
        {
            if (xdcHeader.Validator is null
                || xdcHeader.Validator.Length != 65
                 //Passing an illegal y parity to syscall will cause a fatal error and program can crash
                 || xdcHeader.Validator[64] >= 4)
                return false;

            Address signer = _ethereumEcdsa.RecoverAddress(new Signature(xdcHeader.Validator.AsSpan(0, 64), xdcHeader.Validator[64]), Keccak.Compute(_headerDecoder.Encode(xdcHeader, RlpBehaviors.ForSealing).Bytes));

            header.Author = signer;
        }
        return xdcHeader.Beneficiary == xdcHeader.Author;
    }
}
