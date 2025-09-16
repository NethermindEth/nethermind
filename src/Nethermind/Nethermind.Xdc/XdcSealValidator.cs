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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Nethermind.Xdc;
internal class XdcSealValidator(ISnapshotManager snapshotManager, ISpecProvider specProvider) : ISealValidator
{
    private EthereumEcdsa _ethereumEcdsa = new(0); //Ignore chainId since we don't sign transactions here
    private XdcHeaderDecoder _headerDecoder = new();

    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
    {
        return ValidateParams(parent, header, out _);
    }

    public bool ValidateParams(BlockHeader parent, BlockHeader header, out string error)
    {
        if (header is not XdcBlockHeader xdcHeader)
            throw new ArgumentException($"Only type of {nameof(XdcBlockHeader)} is allowed, but got type {header.GetType().Name}.", nameof(header));
        if (xdcHeader.ExtraConsensusData is null)
        {
            error = "ExtraData doesn't contain required consensus data.";
            return false;
        }

        ExtraFieldsV2 extraFieldsV2 = xdcHeader.ExtraConsensusData!;

        if (extraFieldsV2.CurrentRound <= extraFieldsV2.QuorumCert.ProposedBlockInfo.Round)
        {
            error = "Round number is not greater than the round in the QC.";
            return false;
        }

        //TODO verify QC

        IXdcReleaseSpec xdcSpec = specProvider.GetXdcSpec(xdcHeader); // will throw if no spec found  

        Address[] masternodes;

        if (xdcHeader.IsEpochSwitch(specProvider))
        {
            if (xdcHeader.Nonce != XdcConstants.NonceDropVoteValue)
            {
                error = "Vote nonce in checkpoint block non-zero.";
                return false;
            }
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

            //TODO init masternodes by reading from most recent checkpoint
            masternodes = snapshotManager.CalculateNextEpochMasternodes(xdcHeader);
            if (!xdcHeader.ValidatorsAddress.SetEquals(masternodes))
            {
                error = "Validators does not match what's stored in snapshot minus its penalty.";
                return false;
            }

            if (!xdcHeader.PenaltiesAddress.SetEquals(snapshotManager.GetPenalties(xdcHeader)))
            {
                error = "Penalties does not match.";
                return false;
            }
        }
        else
        {
            if (xdcHeader.Validators?.Length != 0)
            {
                error = "Validators are not empty in non-epoch switch header.";
                return false;
            }
            if (xdcHeader.Penalties?.Length != 0)
            {
                error = "Penalties are not empty in non-epoch switch header.";
                return false;
            }
            //TODO get masternodes from snapshot
            masternodes = snapshotManager.GetMasternodes(xdcHeader);
        }
        
        ulong currentLeaderIndex = (xdcHeader.ExtraConsensusData.CurrentRound % (ulong)xdcSpec.EpochLength % (ulong)masternodes.Length);
        if (masternodes[(int)currentLeaderIndex] != header.Author)
        {
            error = $"Block proposer {header.Author} is not the current leader.";
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
