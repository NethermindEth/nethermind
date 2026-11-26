// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract;

public class AuRaContractSignatureTests
{
    private static IEnumerable<TestCaseData> Functions()
    {
        yield return new TestCaseData(BlockGasLimitContract.BlockGasLimitFunction, "blockGasLimit()", "0x7877a797", "(uint256)");
        yield return new TestCaseData(CertifierContract.CertifiedFunction, "certified(address)", "0xcc1d4c02", "(bool)");
        yield return new TestCaseData(RandomContract.CurrentCollectRoundFunction, "currentCollectRound()", "0x7a3e286b", "(uint256)");
        yield return new TestCaseData(RandomContract.GetCommitAndCipherFunction, "getCommitAndCipher(uint256,address)", "0x695e89f6", "(bytes32,bytes)");
        yield return new TestCaseData(RandomContract.IsCommittedFunction, "isCommitted(uint256,address)", "0xbaf11cab", "(bool)");
        yield return new TestCaseData(RandomContract.IsCommitPhaseFunction, "isCommitPhase()", "0x74ce9067", "(bool)");
        yield return new TestCaseData(RandomContract.SentRevealFunction, "sentReveal(uint256,address)", "0x63f160e6", "(bool)");
        yield return new TestCaseData(RegisterContract.GetAddressFunction, "getAddress(bytes32,string)", "0x6795dbcd", "(address)");
        yield return new TestCaseData(RewardContract.RewardFunction, "reward(address[],uint16[])", "0xf91c2898", "(address[],uint256[])");
        yield return new TestCaseData(TransactionPermissionContractV1.AllowedTxTypesFunction, "allowedTxTypes(address)", "0xe1751221", "(uint32)");
        yield return new TestCaseData(TransactionPermissionContractV2.AllowedTxTypesFunction, "allowedTxTypes(address,address,uint256)", "0xd4b03ee0", "(uint32,bool)");
        yield return new TestCaseData(TransactionPermissionContractV3.AllowedTxTypesFunction, "allowedTxTypes(address,address,uint256,uint256,bytes)", "0xb9056afa", "(uint32,bool)");
        yield return new TestCaseData(TransactionPermissionContractV4.AllowedTxTypesFunction, "allowedTxTypes(address,address,uint256,uint256,uint256,uint256,bytes)", "0xe4e3b5e5", "(uint32,bool)");
        yield return new TestCaseData(TransactionPermissionContract.ContractVersionFunction, "contractVersion()", "0xa0a8e460", "(uint256)");
        yield return new TestCaseData(TxPriorityContract.GetPrioritiesFunction, "getPriorities()", "0xa0daeed2", "((address,bytes4,uint256)[])");
        yield return new TestCaseData(TxPriorityContract.GetSendersWhitelistFunction, "getSendersWhitelist()", "0x338cd6bd", "(address[])");
        yield return new TestCaseData(TxPriorityContract.GetMinGasPricesFunction, "getMinGasPrices()", "0x7d616fd3", "((address,bytes4,uint256)[])");
        yield return new TestCaseData(ValidatorContract.FinalizeChangeFunction, "finalizeChange()", "0x75286211", "()");
        yield return new TestCaseData(ValidatorContract.GetValidatorsFunction, "getValidators()", "0xb7ab4db5", "(address[])");
        yield return new TestCaseData(ValidatorContract.EmitInitiateChangeCallableFunction, "emitInitiateChangeCallable()", "0x3d3b5458", "(bool)");
        yield return new TestCaseData(ValidatorContract.ShouldValidatorReportFunction, "shouldValidatorReport(address,address,uint256)", "0xcbd2d528", "(bool)");
    }

    private static IEnumerable<TestCaseData> Calls()
    {
        yield return new TestCaseData(RandomContract.CommitHashSignature, "commitHash(bytes32,bytes)", "0x0b61ba85");
        yield return new TestCaseData(RandomContract.RevealNumberSignature, "revealNumber(uint256)", "0xfe7d567d");
        yield return new TestCaseData(ReportingValidatorContract.ReportMaliciousSignature, "reportMalicious(address,uint256,bytes)", "0xc476dd40");
        yield return new TestCaseData(ReportingValidatorContract.ReportBenignSignature, "reportBenign(address,uint256)", "0xd69f13bb");
        yield return new TestCaseData(ValidatorContract.EmitInitiateChangeSignature, "emitInitiateChange()", "0x93b4e25e");
    }

    private static IEnumerable<TestCaseData> Events()
    {
        yield return new TestCaseData(TxPriorityContract.PrioritySetEvent, TxPriorityContract.DestinationData, "PrioritySet(address,bytes4,uint256)", "0x896fc6a8eb054c92d480f4d32ec773e5ebe77109ad3b179f3e97ac78cf3e7cb7", "(uint256)");
        yield return new TestCaseData(TxPriorityContract.MinGasPriceSetEvent, TxPriorityContract.DestinationData, "MinGasPriceSet(address,bytes4,uint256)", "0x7c3ecffa528bfccc8dd5c2454093da1c08757c960b1e5425afb9cb3b18439028", "(uint256)");
        yield return new TestCaseData(TxPriorityContract.SendersWhitelistSetEvent, TxPriorityContract.AddressesData, "SendersWhitelistSet(address[])", "0xf3a1b17f96de235ad0ae63327b65739025bc92dd4bc161c9d8b661cb449ec62d", "(address[])");
        yield return new TestCaseData(ValidatorContract.InitiateChangeEvent, ValidatorContract.InitiateChangeData, "InitiateChange(bytes32,address[])", "0x55252fa6eee4741b4e24a74a70e9c11fd2c2281df8d6ea13126ff845f7825c89", "(address[])");
    }

    [TestCaseSource(nameof(Functions))]
    public void Function_matches_pinned_abi<TArgs, TResult>(ContractFunction<TArgs, TResult> function, string signature, string selector, string returns) =>
        AbiAssert.FunctionMatches(function, signature, selector, returns);

    [TestCaseSource(nameof(Calls))]
    public void Call_matches_pinned_abi<TArgs>(AbiSignature<TArgs> call, string signature, string selector)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.ToString(), Is.EqualTo(signature));
            Assert.That(call.MethodId.ToHexString(true), Is.EqualTo(selector));
        }
    }

    [TestCaseSource(nameof(Events))]
    public void Event_matches_pinned_abi<TEvent, TData>(AbiSignature<TEvent> @event, AbiSignature<TData> data, string signature, string topic, string dataSignature)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(@event.ToString(), Is.EqualTo(signature));
            Assert.That(@event.Hash.ToString(), Is.EqualTo(topic));
            Assert.That(data.ToString(), Is.EqualTo(dataSignature));
        }
    }
}
