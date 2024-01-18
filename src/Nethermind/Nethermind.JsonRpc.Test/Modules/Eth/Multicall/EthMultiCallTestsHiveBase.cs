// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;
using ResultType = Nethermind.Core.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthMultiCallTestsHiveBase
{

    [Test]
    public async Task TestmulticallAddMoreNonDefinedBlockStateCallsThanFitButNowWithFit()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xa\"},\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506000366060484641444543425a3a60014361002c919061009b565b406040516020016100469a99989796959493929190610138565b6040516020818303038152906040529050915050805190602001f35b6000819050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b60006100a682610062565b91506100b183610062565b92508282039050818111156100c9576100c861006c565b5b92915050565b6100d881610062565b82525050565b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b6000610109826100de565b9050919050565b610119816100fe565b82525050565b6000819050919050565b6101328161011f565b82525050565b60006101408201905061014e600083018d6100cf565b61015b602083018c6100cf565b610168604083018b610110565b610175606083018a6100cf565b61018260808301896100cf565b61018f60a08301886100cf565b61019c60c08301876100cf565b6101a960e08301866100cf565b6101b76101008301856100cf565b6101c5610120830184610129565b9b9a505050505050505050505056fea26469706673582212205139ae3ba8d46d11c29815d001b725f9840c90e330884ed070958d5af4813d8764736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]},{\"blockOverrides\":{\"number\":\"0x14\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallAddMoreNonDefinedBlockStateCallsThanFitButNowWithFit");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallAddMoreNonDefinedBlockStateCallsThanFit()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xa\"},\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506000366060484641444543425a3a60014361002c919061009b565b406040516020016100469a99989796959493929190610138565b6040516020818303038152906040529050915050805190602001f35b6000819050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b60006100a682610062565b91506100b183610062565b92508282039050818111156100c9576100c861006c565b5b92915050565b6100d881610062565b82525050565b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b6000610109826100de565b9050919050565b610119816100fe565b82525050565b6000819050919050565b6101328161011f565b82525050565b60006101408201905061014e600083018d6100cf565b61015b602083018c6100cf565b610168604083018b610110565b610175606083018a6100cf565b61018260808301896100cf565b61018f60a08301886100cf565b61019c60c08301876100cf565b6101a960e08301866100cf565b6101b76101008301856100cf565b6101c5610120830184610129565b9b9a505050505050505050505056fea26469706673582212205139ae3ba8d46d11c29815d001b725f9840c90e330884ed070958d5af4813d8764736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]},{\"blockOverrides\":{\"number\":\"0xb\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallAddMoreNonDefinedBlockStateCallsThanFit");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBasefeeTooLowWithValidation38012()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"baseFeePerGas\":\"0xa\"},\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"}},\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"maxFeePerGas\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\"}]}],\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBasefeeTooLowWithValidation38012");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data.First().PrevRandao, Is.EqualTo(new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000").BytesToArray()));


        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBasefeeTooLowWithoutValidation38012()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"baseFeePerGas\":\"0xa\"},\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"}},\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"maxFeePerGas\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBasefeeTooLowWithoutValidation38012");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockNumOrder38020()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xc\"},\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x4360005260206000f3\"}]},{\"blockOverrides\":{\"number\":\"0xb\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"input\":\"0x4360005260206000f3\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockNumOrder38020");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockOverrideReflectedInContractSimple()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xa\",\"time\":\"0x64\"}},{\"blockOverrides\":{\"number\":\"0x14\",\"time\":\"0x65\"}},{\"blockOverrides\":{\"number\":\"0x15\",\"time\":\"0xc8\"}}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockOverrideReflectedInContractSimple");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockOverrideReflectedInContract()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xa\",\"time\":\"0x64\",\"gasLimit\":\"0xa\",\"feeRecipient\":\"0xc000000000000000000000000000000000000000\",\"prevRandao\":\"0x0000000000000000000000000000000000000000000000000000000000000012\",\"baseFeePerGas\":\"0xa\"},\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506000366060484641444543425a3a60014361002c919061009b565b406040516020016100469a99989796959493929190610138565b6040516020818303038152906040529050915050805190602001f35b6000819050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b60006100a682610062565b91506100b183610062565b92508282039050818111156100c9576100c861006c565b5b92915050565b6100d881610062565b82525050565b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b6000610109826100de565b9050919050565b610119816100fe565b82525050565b6000819050919050565b6101328161011f565b82525050565b60006101408201905061014e600083018d6100cf565b61015b602083018c6100cf565b610168604083018b610110565b610175606083018a6100cf565b61018260808301896100cf565b61018f60a08301886100cf565b61019c60c08301876100cf565b6101a960e08301866100cf565b6101b76101008301856100cf565b6101c5610120830184610129565b9b9a505050505050505050505056fea26469706673582212205139ae3ba8d46d11c29815d001b725f9840c90e330884ed070958d5af4813d8764736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]},{\"blockOverrides\":{\"number\":\"0x14\",\"time\":\"0xc8\",\"gasLimit\":\"0x14\",\"feeRecipient\":\"0xc100000000000000000000000000000000000000\",\"prevRandao\":\"0x0000000000000000000000000000000000000000000000000000000000001234\",\"baseFeePerGas\":\"0x14\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]},{\"blockOverrides\":{\"number\":\"0x15\",\"time\":\"0x12c\",\"gasLimit\":\"0x15\",\"feeRecipient\":\"0xc200000000000000000000000000000000000000\",\"prevRandao\":\"0x0000000000000000000000000000000000000000000000000000000000001234\",\"baseFeePerGas\":\"0x1e\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockOverrideReflectedInContract");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockTimestampAutoIncrement()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"time\":\"0xb\"}},{\"blockOverrides\":{}},{\"blockOverrides\":{\"time\":\"0xc\"}},{\"blockOverrides\":{}}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockTimestampAutoIncrement");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockTimestampNonIncrement()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"time\":\"0xc\"}},{\"blockOverrides\":{\"time\":\"0xc\"}}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockTimestampNonIncrement");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockTimestampOrder38021()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"time\":\"0xc\"}},{\"blockOverrides\":{\"time\":\"0xb\"}}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockTimestampOrder38021");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockTimestampsIncrementing()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"time\":\"0xb\"}},{\"blockOverrides\":{\"time\":\"0xc\"}}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockTimestampsIncrementing");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockhashComplex()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xa\"},\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x1e8480\"},\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x6080604052348015600f57600080fd5b506004361060285760003560e01c8063ee82ac5e14602d575b600080fd5b60436004803603810190603f91906098565b6057565b604051604e919060d7565b60405180910390f35b600081409050919050565b600080fd5b6000819050919050565b6078816067565b8114608257600080fd5b50565b6000813590506092816071565b92915050565b60006020828403121560ab5760aa6062565b5b600060b7848285016085565b91505092915050565b6000819050919050565b60d18160c0565b82525050565b600060208201905060ea600083018460ca565b9291505056fea2646970667358221220a4d7face162688805e99e86526524ac3dadfb01cc29366d0d68b70dadcf01afe64736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xee82ac5e0000000000000000000000000000000000000000000000000000000000000001\"}]},{\"blockOverrides\":{\"number\":\"0x14\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xee82ac5e0000000000000000000000000000000000000000000000000000000000000010\"}]},{\"blockOverrides\":{\"number\":\"0x1e\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xee82ac5e000000000000000000000000000000000000000000000000000000000000001d\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockhashComplex");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockhashSimple()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x6080604052348015600f57600080fd5b506004361060285760003560e01c8063ee82ac5e14602d575b600080fd5b60436004803603810190603f91906098565b6057565b604051604e919060d7565b60405180910390f35b600081409050919050565b600080fd5b6000819050919050565b6078816067565b8114608257600080fd5b50565b6000813590506092816071565b92915050565b60006020828403121560ab5760aa6062565b5b600060b7848285016085565b91505092915050565b6000819050919050565b60d18160c0565b82525050565b600060208201905060ea600083018460ca565b9291505056fea2646970667358221220a4d7face162688805e99e86526524ac3dadfb01cc29366d0d68b70dadcf01afe64736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xee82ac5e0000000000000000000000000000000000000000000000000000000000000001\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockhashSimple");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallBlockhashStartBeforeHead()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xa\"},\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x1e8480\"},\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x6080604052348015600f57600080fd5b506004361060285760003560e01c8063ee82ac5e14602d575b600080fd5b60436004803603810190603f91906098565b6057565b604051604e919060d7565b60405180910390f35b600081409050919050565b600080fd5b6000819050919050565b6078816067565b8114608257600080fd5b50565b6000813590506092816071565b92915050565b60006020828403121560ab5760aa6062565b5b600060b7848285016085565b91505092915050565b6000819050919050565b60d18160c0565b82525050565b600060208201905060ea600083018460ca565b9291505056fea2646970667358221220a4d7face162688805e99e86526524ac3dadfb01cc29366d0d68b70dadcf01afe64736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xee82ac5e0000000000000000000000000000000000000000000000000000000000000002\"}]},{\"blockOverrides\":{\"number\":\"0x14\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xee82ac5e0000000000000000000000000000000000000000000000000000000000000013\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallBlockhashStartBeforeHead");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallCheckInvalidNonce()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x4e20\"}}},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc000000000000000000000000000000000000000\",\"nonce\":\"0x0\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x1\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x0\"}]}],\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallCheckInvalidNonce");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallCheckThatBalanceIsThereAfterNewBlock()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x2710\"},\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c000000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c100000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c000000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c100000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallCheckThatBalanceIsThereAfterNewBlock");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallCheckThatNonceIncreases()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x4e20\"}}},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc000000000000000000000000000000000000000\",\"nonce\":\"0x0\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x1\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x2\"}]}],\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallCheckThatNonceIncreases");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallEmptyCallsAndOverridesMulticall()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{},\"calls\":[{}]},{\"stateOverrides\":{},\"calls\":[{}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallEmptyCallsAndOverridesMulticall");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallEthSendShouldNotProduceLogsByDefault()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallEthSendShouldNotProduceLogsByDefault");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallEthSendShouldNotProduceLogsOnRevert()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"},\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x608060405260006042576040517f08c379a0000000000000000000000000000000000000000000000000000000008152600401603990609d565b60405180910390fd5b005b600082825260208201905092915050565b7f416c7761797320726576657274696e6720636f6e747261637400000000000000600082015250565b600060896019836044565b91506092826055565b602082019050919050565b6000602082019050818103600083015260b481607e565b905091905056fea264697066735822122005cbbbc709291f66fadc17416c1b0ed4d72941840db11468a21b8e1a0362024c64736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallEthSendShouldNotProduceLogsOnRevert");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallEthSendShouldProduceLogs()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallEthSendShouldProduceLogs");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallEthSendShouldProduceMoreLogsOnForward()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"},\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x60806040526004361061001e5760003560e01c80634b64e49214610023575b600080fd5b61003d6004803603810190610038919061011f565b61003f565b005b60008173ffffffffffffffffffffffffffffffffffffffff166108fc349081150290604051600060405180830381858888f193505050509050806100b8576040517f08c379a00000000000000000000000000000000000000000000000000000000081526004016100af906101a9565b60405180910390fd5b5050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100ec826100c1565b9050919050565b6100fc816100e1565b811461010757600080fd5b50565b600081359050610119816100f3565b92915050565b600060208284031215610135576101346100bc565b5b60006101438482850161010a565b91505092915050565b600082825260208201905092915050565b7f4661696c656420746f2073656e64204574686572000000000000000000000000600082015250565b600061019360148361014c565b915061019e8261015d565b602082019050919050565b600060208201905081810360008301526101c281610186565b905091905056fea2646970667358221220563acd6f5b8ad06a3faf5c27fddd0ecbc198408b99290ce50d15c2cf7043694964736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\",\"input\":\"0x4b64e4920000000000000000000000000000000000000000000000000000000000000100\"}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallEthSendShouldProduceMoreLogsOnForward");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallEthSendShouldProduceNoLogsOnForwardRevert()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"},\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x60806040526004361061001e5760003560e01c80634b64e49214610023575b600080fd5b61003d6004803603810190610038919061011f565b61003f565b005b60008173ffffffffffffffffffffffffffffffffffffffff166108fc349081150290604051600060405180830381858888f193505050509050806100b8576040517f08c379a00000000000000000000000000000000000000000000000000000000081526004016100af906101a9565b60405180910390fd5b5050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100ec826100c1565b9050919050565b6100fc816100e1565b811461010757600080fd5b50565b600081359050610119816100f3565b92915050565b600060208284031215610135576101346100bc565b5b60006101438482850161010a565b91505092915050565b600082825260208201905092915050565b7f4661696c656420746f2073656e64204574686572000000000000000000000000600082015250565b600061019360148361014c565b915061019e8261015d565b602082019050919050565b600060208201905081810360008301526101c281610186565b905091905056fea2646970667358221220563acd6f5b8ad06a3faf5c27fddd0ecbc198408b99290ce50d15c2cf7043694964736f6c63430008120033\"},\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x608060405260006042576040517f08c379a0000000000000000000000000000000000000000000000000000000008152600401603990609d565b60405180910390fd5b005b600082825260208201905092915050565b7f416c7761797320726576657274696e6720636f6e747261637400000000000000600082015250565b600060896019836044565b91506092826055565b602082019050919050565b6000602082019050818103600083015260b481607e565b905091905056fea264697066735822122005cbbbc709291f66fadc17416c1b0ed4d72941840db11468a21b8e1a0362024c64736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\",\"input\":\"0x4b64e492c200000000000000000000000000000000000000000000000000000000000000\"}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallEthSendShouldProduceNoLogsOnForwardRevert");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallFeeRecipientReceivingFunds()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xa\",\"feeRecipient\":\"0xc200000000000000000000000000000000000000\",\"baseFeePerGas\":\"0xa\"},\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x1e8480\"},\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"maxFeePerGas\":\"0xa\",\"maxPriorityFeePerGas\":\"0xa\",\"nonce\":\"0x0\",\"input\":\"0x\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x1\",\"input\":\"0xf8b2cb4f000000000000000000000000c000000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x2\",\"input\":\"0xf8b2cb4f000000000000000000000000c200000000000000000000000000000000000000\"}]}],\"traceTransfers\":true,\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallFeeRecipientReceivingFunds");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallGasFeesAndValueError38014WithValidation()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallGasFeesAndValueError38014WithValidation");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallGasFeesAndValueError38014()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallGasFeesAndValueError38014");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallGetBlockProperties()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc100000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506000366060484641444543425a3a60014361002c919061009b565b406040516020016100469a99989796959493929190610138565b6040516020818303038152906040529050915050805190602001f35b6000819050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b60006100a682610062565b91506100b183610062565b92508282039050818111156100c9576100c861006c565b5b92915050565b6100d881610062565b82525050565b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b6000610109826100de565b9050919050565b610119816100fe565b82525050565b6000819050919050565b6101328161011f565b82525050565b60006101408201905061014e600083018d6100cf565b61015b602083018c6100cf565b610168604083018b610110565b610175606083018a6100cf565b61018260808301896100cf565b61018f60a08301886100cf565b61019c60c08301876100cf565b6101a960e08301866100cf565b6101b76101008301856100cf565b6101c5610120830184610129565b9b9a505050505050505050505056fea26469706673582212205139ae3ba8d46d11c29815d001b725f9840c90e330884ed070958d5af4813d8764736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallGetBlockProperties");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallInstrictGas38013()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"gas\":\"0x0\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallInstrictGas38013");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallLogs()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff80600080a1600080f3\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0x6057361d0000000000000000000000000000000000000000000000000000000000000005\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallLogs");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallMoveAccountTwice()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x3e8\"},\"0xc200000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"},\"0xc300000000000000000000000000000000000000\":{\"balance\":\"0xbb8\"},\"0xc400000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033\"}}},{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0xbb8\",\"MovePrecompileToAddress\":\"0xc200000000000000000000000000000000000000\"},\"0xc200000000000000000000000000000000000000\":{\"balance\":\"0xfa0\",\"MovePrecompileToAddress\":\"0xc300000000000000000000000000000000000000\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc400000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c000000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc400000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c200000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc400000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c300000000000000000000000000000000000000\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallMoveAccountTwice");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallMoveEcrecoverAndCall()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000000001\",\"input\":\"0x4554480000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007b45544800000000000000000000000000000000000000000000000000000000004554480000000000000000000000000000000000000000000000000000000000\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000000001\",\"input\":\"0x1c8aff950685c2ed4bc3174f3472287b56d9517b9c948127319a09a7a36deac8000000000000000000000000000000000000000000000000000000000000001cb7cf302145348387b9e69fde82d8e634a0f8761e78da3bfa059efced97cbed0d2a66b69167cafe0ccfc726aec6ee393fea3cf0e4f3f9c394705e0f56d9bfe1c9\"}]},{\"stateOverrides\":{\"0x0000000000000000000000000000000000000001\":{\"MovePrecompileToAddress\":\"0x0000000000000000000000000000000000123456\"}},\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000123456\",\"input\":\"0x4554480000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007b45544800000000000000000000000000000000000000000000000000000000004554480000000000000000000000000000000000000000000000000000000000\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000123456\",\"input\":\"0x1c8aff950685c2ed4bc3174f3472287b56d9517b9c948127319a09a7a36deac8000000000000000000000000000000000000000000000000000000000000001cb7cf302145348387b9e69fde82d8e634a0f8761e78da3bfa059efced97cbed0d2a66b69167cafe0ccfc726aec6ee393fea3cf0e4f3f9c394705e0f56d9bfe1c9\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallMoveEcrecoverAndCall");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallMoveToAddressItselfReference38022()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x30d40\"},\"0xc100000000000000000000000000000000000000\":{\"MovePrecompileToAddress\":\"0xc100000000000000000000000000000000000000\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x1\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallMoveToAddressItselfReference38022");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallMoveTwoAccountsToSame38023()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0x0000000000000000000000000000000000000001\":{\"MovePrecompileToAddress\":\"0xc200000000000000000000000000000000000000\"},\"0x0000000000000000000000000000000000000002\":{\"MovePrecompileToAddress\":\"0xc200000000000000000000000000000000000000\"}}}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallMoveTwoAccountsToSame38023");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallMoveTwoNonPrecompilesAccountsToSame()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0x0100000000000000000000000000000000000000\":{\"MovePrecompileToAddress\":\"0xc200000000000000000000000000000000000000\"},\"0x0200000000000000000000000000000000000000\":{\"MovePrecompileToAddress\":\"0xc200000000000000000000000000000000000000\"}}}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallMoveTwoNonPrecompilesAccountsToSame");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallOverrideAddressTwiceInSeparateBlockStateCalls()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]},{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallOverrideAddressTwiceInSeparateBlockStateCalls");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallOverrideAddressTwice()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"code\":\"0x608060405260006042576040517f08c379a0000000000000000000000000000000000000000000000000000000008152600401603990609d565b60405180910390fd5b005b600082825260208201905092915050565b7f416c7761797320726576657274696e6720636f6e747261637400000000000000600082015250565b600060896019836044565b91506092826055565b602082019050919050565b6000602082019050818103600083015260b481607e565b905091905056fea264697066735822122005cbbbc709291f66fadc17416c1b0ed4d72941840db11468a21b8e1a0362024c64736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallOverrideAddressTwice");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallOverrideAllInBlockStateCalls()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0x3e9\",\"time\":\"0x3eb\",\"gasLimit\":\"0x3ec\",\"feeRecipient\":\"0xc200000000000000000000000000000000000000\",\"prevRandao\":\"0xc300000000000000000000000000000000000000000000000000000000000000\",\"baseFeePerGas\":\"0x3ef\"}}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallOverrideAllInBlockStateCalls");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallOverrideBlockNum()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"number\":\"0xb\"},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"input\":\"0x4360005260206000f3\"}]},{\"blockOverrides\":{\"number\":\"0xc\"},\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"input\":\"0x4360005260206000f3\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallOverrideBlockNum");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallOverrideEcrecover()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0x0000000000000000000000000000000000000001\":{\"code\":\"0x608060405234801561001057600080fd5b506004361061003a5760003560e01c806305fdbc81146101ee578063c00692601461020a5761003b565b5b600036606060008060008086868101906100559190610462565b93509350935093506000806000868686866040516020016100799493929190610520565b60405160208183030381529060405280519060200120815260200190815260200160002060009054906101000a900473ffffffffffffffffffffffffffffffffffffffff169050600073ffffffffffffffffffffffffffffffffffffffff168173ffffffffffffffffffffffffffffffffffffffff16036101bb576000806212345673ffffffffffffffffffffffffffffffffffffffff166127108b8b6040516101249291906105ad565b60006040518083038160008787f1925050503d8060008114610162576040519150601f19603f3d011682016040523d82523d6000602084013e610167565b606091505b5091509150816101ac576040517f08c379a00000000000000000000000000000000000000000000000000000000081526004016101a39061066f565b60405180910390fd5b809750505050505050506101e3565b806040516020016101cc9190610709565b604051602081830303815290604052955050505050505b915050805190602001f35b6102086004803603810190610203919061093a565b610226565b005b610224600480360381019061021f9190610983565b6102ec565b005b60005b81518110156102e8576102d5828281518110610248576102476109fe565b5b602002602001015160000151838381518110610267576102666109fe565b5b602002602001015160200151848481518110610286576102856109fe565b5b6020026020010151604001518585815181106102a5576102a46109fe565b5b6020026020010151606001518686815181106102c4576102c36109fe565b5b6020026020010151608001516102ec565b80806102e090610a66565b915050610229565b5050565b600073ffffffffffffffffffffffffffffffffffffffff168173ffffffffffffffffffffffffffffffffffffffff160361035b576040517f08c379a000000000000000000000000000000000000000000000000000000000815260040161035290610afa565b60405180910390fd5b80600080878787876040516020016103769493929190610520565b60405160208183030381529060405280519060200120815260200190815260200160002060006101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff1602179055505050505050565b6000604051905090565b600080fd5b600080fd5b6000819050919050565b610406816103f3565b811461041157600080fd5b50565b600081359050610423816103fd565b92915050565b600060ff82169050919050565b61043f81610429565b811461044a57600080fd5b50565b60008135905061045c81610436565b92915050565b6000806000806080858703121561047c5761047b6103e9565b5b600061048a87828801610414565b945050602061049b8782880161044d565b93505060406104ac87828801610414565b92505060606104bd87828801610414565b91505092959194509250565b6000819050919050565b6104e46104df826103f3565b6104c9565b82525050565b60008160f81b9050919050565b6000610502826104ea565b9050919050565b61051a61051582610429565b6104f7565b82525050565b600061052c82876104d3565b60208201915061053c8286610509565b60018201915061054c82856104d3565b60208201915061055c82846104d3565b60208201915081905095945050505050565b600081905092915050565b82818337600083830152505050565b6000610594838561056e565b93506105a1838584610579565b82840190509392505050565b60006105ba828486610588565b91508190509392505050565b600082825260208201905092915050565b7f6661696c656420746f2063616c6c206d6f7665642065637265636f766572206160008201527f742061646472657373203078303030303030303030303030303030303030303060208201527f3030303030303030303030303030313233343536000000000000000000000000604082015250565b60006106596054836105c6565b9150610664826105d7565b606082019050919050565b600060208201905081810360008301526106888161064c565b9050919050565b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006106ba8261068f565b9050919050565b60008160601b9050919050565b60006106d9826106c1565b9050919050565b60006106eb826106ce565b9050919050565b6107036106fe826106af565b6106e0565b82525050565b600061071582846106f2565b60148201915081905092915050565b600080fd5b6000601f19601f8301169050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052604160045260246000fd5b61077282610729565b810181811067ffffffffffffffff821117156107915761079061073a565b5b80604052505050565b60006107a46103df565b90506107b08282610769565b919050565b600067ffffffffffffffff8211156107d0576107cf61073a565b5b602082029050602081019050919050565b600080fd5b600080fd5b6107f4816106af565b81146107ff57600080fd5b50565b600081359050610811816107eb565b92915050565b600060a0828403121561082d5761082c6107e6565b5b61083760a061079a565b9050600061084784828501610414565b600083015250602061085b8482850161044d565b602083015250604061086f84828501610414565b604083015250606061088384828501610414565b606083015250608061089784828501610802565b60808301525092915050565b60006108b66108b1846107b5565b61079a565b90508083825260208201905060a084028301858111156108d9576108d86107e1565b5b835b8181101561090257806108ee8882610817565b84526020840193505060a0810190506108db565b5050509392505050565b600082601f83011261092157610920610724565b5b81356109318482602086016108a3565b91505092915050565b6000602082840312156109505761094f6103e9565b5b600082013567ffffffffffffffff81111561096e5761096d6103ee565b5b61097a8482850161090c565b91505092915050565b600080600080600060a0868803121561099f5761099e6103e9565b5b60006109ad88828901610414565b95505060206109be8882890161044d565b94505060406109cf88828901610414565b93505060606109e088828901610414565b92505060806109f188828901610802565b9150509295509295909350565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052603260045260246000fd5b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b6000819050919050565b6000610a7182610a5c565b91507fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8203610aa357610aa2610a2d565b5b600182019050919050565b7f72657475726e20616464726573732063616e6e6f742062652030783000000000600082015250565b6000610ae4601c836105c6565b9150610aef82610aae565b602082019050919050565b60006020820190508181036000830152610b1381610ad7565b905091905056fea2646970667358221220154f5b68ccfa5be744e7245765a3530dac4035052284a68b5dded1945b45075e64736f6c63430008120033\",\"MovePrecompileToAddress\":\"0x0000000000000000000000000000000000123456\"},\"0xc100000000000000000000000000000000000000\":{\"balance\":\"0x30d40\"}},\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000123456\",\"input\":\"0x4554480000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007b45544800000000000000000000000000000000000000000000000000000000004554480000000000000000000000000000000000000000000000000000000000\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000123456\",\"input\":\"0x1c8aff950685c2ed4bc3174f3472287b56d9517b9c948127319a09a7a36deac8000000000000000000000000000000000000000000000000000000000000001cb7cf302145348387b9e69fde82d8e634a0f8761e78da3bfa059efced97cbed0d2a66b69167cafe0ccfc726aec6ee393fea3cf0e4f3f9c394705e0f56d9bfe1c9\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000000001\",\"input\":\"0xc00692604554480000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007b45544800000000000000000000000000000000000000000000000000000000004554480000000000000000000000000000000000000000000000000000000000000000000000000000000000d8da6bf26964af9d7eed9e03e53415d37aa96045\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000000001\",\"input\":\"0x4554480000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007b45544800000000000000000000000000000000000000000000000000000000004554480000000000000000000000000000000000000000000000000000000000\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000000001\",\"input\":\"0x1c8aff950685c2ed4bc3174f3472287b56d9517b9c948127319a09a7a36deac8000000000000000000000000000000000000000000000000000000000000001cb7cf302145348387b9e69fde82d8e634a0f8761e78da3bfa059efced97cbed0d2a66b69167cafe0ccfc726aec6ee393fea3cf0e4f3f9c394705e0f56d9bfe1c9\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000000001\",\"input\":\"0x4554480000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007b45544800000000000000000000000000000000000000000000000000000000004554490000000000000000000000000000000000000000000000000000000000\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallOverrideEcrecover");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallOverrideIdentity()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0x0000000000000000000000000000000000000004\":{\"code\":\"0x\",\"MovePrecompileToAddress\":\"0x0000000000000000000000000000000000123456\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000123456\",\"input\":\"0x1234\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000000004\",\"input\":\"0x1234\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallOverrideIdentity");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallOverrideSha256()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0x0000000000000000000000000000000000000002\":{\"code\":\"0x\",\"MovePrecompileToAddress\":\"0x0000000000000000000000000000000000123456\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000123456\",\"input\":\"0x1234\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0x0000000000000000000000000000000000000002\",\"input\":\"0x1234\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallOverrideSha256");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallPrecompileIsSendingTransaction()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0x0000000000000000000000000000000000000004\",\"to\":\"0x0000000000000000000000000000000000000002\",\"input\":\"0x1234\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallPrecompileIsSendingTransaction");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallRunOutOfGasInBlock38015()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"blockOverrides\":{\"gasLimit\":\"0x16e360\"},\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x1e8480\"},\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063815b8ab414610030575b600080fd5b61004a600480360381019061004591906100b6565b61004c565b005b60005a90505b60011561007657815a826100669190610112565b106100715750610078565b610052565b505b50565b600080fd5b6000819050919050565b61009381610080565b811461009e57600080fd5b50565b6000813590506100b08161008a565b92915050565b6000602082840312156100cc576100cb61007b565b5b60006100da848285016100a1565b91505092915050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b600061011d82610080565b915061012883610080565b92508282039050818111156101405761013f6100e3565b5b9291505056fea2646970667358221220a659ba4db729a6ee4db02fcc5c1118db53246b0e5e686534fc9add6f2e93faec64736f6c63430008120033\"}}},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0x815b8ab400000000000000000000000000000000000000000000000000000000000f4240\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0x815b8ab400000000000000000000000000000000000000000000000000000000000f4240\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallRunOutOfGasInBlock38015");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSelfDestructingStateOverride()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x6080604052348015600f57600080fd5b506004361060285760003560e01c806383197ef014602d575b600080fd5b60336035565b005b600073ffffffffffffffffffffffffffffffffffffffff16fffea26469706673582212208e566fde20a17fff9658b9b1db37e27876fd8934ccf9b2aa308cabd37698681f64736f6c63430008120033\"},\"0xc300000000000000000000000000000000000000\":{\"code\":\"0x73000000000000000000000000000000000000000030146080604052600436106100355760003560e01c8063dce4a4471461003a575b600080fd5b610054600480360381019061004f91906100f8565b61006a565b60405161006191906101b5565b60405180910390f35b6060813b6040519150601f19601f602083010116820160405280825280600060208401853c50919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100c58261009a565b9050919050565b6100d5816100ba565b81146100e057600080fd5b50565b6000813590506100f2816100cc565b92915050565b60006020828403121561010e5761010d610095565b5b600061011c848285016100e3565b91505092915050565b600081519050919050565b600082825260208201905092915050565b60005b8381101561015f578082015181840152602081019050610144565b60008484015250505050565b6000601f19601f8301169050919050565b600061018782610125565b6101918185610130565b93506101a1818560208601610141565b6101aa8161016b565b840191505092915050565b600060208201905081810360008301526101cf818461017c565b90509291505056fea26469706673582212206a5f0cd9f230619fa520fc4b9d4b518643258cad412f2fa33945ce528b4b895164736f6c63430008120033\"}}},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc300000000000000000000000000000000000000\",\"input\":\"0xdce4a447000000000000000000000000c200000000000000000000000000000000000000\"}]},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0x83197ef0\"}]},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc300000000000000000000000000000000000000\",\"input\":\"0xdce4a447000000000000000000000000c200000000000000000000000000000000000000\"}]},{\"stateOverrides\":{\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x6080604052348015600f57600080fd5b506004361060285760003560e01c806383197ef014602d575b600080fd5b60336035565b005b600073ffffffffffffffffffffffffffffffffffffffff16fffea26469706673582212208e566fde20a17fff9658b9b1db37e27876fd8934ccf9b2aa308cabd37698681f64736f6c63430008120033\"}}},{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc300000000000000000000000000000000000000\",\"input\":\"0xdce4a447000000000000000000000000c200000000000000000000000000000000000000\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSelfDestructingStateOverride");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSetReadStorage()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b50600436106100365760003560e01c80632e64cec11461003b5780636057361d14610059575b600080fd5b610043610075565b60405161005091906100d9565b60405180910390f35b610073600480360381019061006e919061009d565b61007e565b005b60008054905090565b8060008190555050565b60008135905061009781610103565b92915050565b6000602082840312156100b3576100b26100fe565b5b60006100c184828501610088565b91505092915050565b6100d3816100f4565b82525050565b60006020820190506100ee60008301846100ca565b92915050565b6000819050919050565b600080fd5b61010c816100f4565b811461011757600080fd5b5056fea2646970667358221220404e37f487a89a932dca5e77faaf6ca2de3b991f93d230604b1b8daaef64766264736f6c63430008070033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0x6057361d0000000000000000000000000000000000000000000000000000000000000005\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0x2e64cec1\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSetReadStorage");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSimpleNoFundsWithBalanceQuerying()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc200000000000000000000000000000000000000\":{\"code\":\"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c000000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c100000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c000000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c100000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c000000000000000000000000000000000000000\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"input\":\"0xf8b2cb4f000000000000000000000000c100000000000000000000000000000000000000\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSimpleNoFundsWithBalanceQuerying");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSimpleNoFundsWithValidationWithoutNonces()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\",\"nonce\":\"0x0\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSimpleNoFundsWithValidationWithoutNonces");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSimpleNoFundsWithValidation()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\",\"nonce\":\"0x0\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"value\":\"0x3e8\",\"nonce\":\"0x1\"}]}],\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSimpleNoFundsWithValidation");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSimpleNoFunds()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSimpleNoFunds");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSimpleSendFromContractNoBalance()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"code\":\"0x60806040526004361061001e5760003560e01c80634b64e49214610023575b600080fd5b61003d6004803603810190610038919061011f565b61003f565b005b60008173ffffffffffffffffffffffffffffffffffffffff166108fc349081150290604051600060405180830381858888f193505050509050806100b8576040517f08c379a00000000000000000000000000000000000000000000000000000000081526004016100af906101a9565b60405180910390fd5b5050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100ec826100c1565b9050919050565b6100fc816100e1565b811461010757600080fd5b50565b600081359050610119816100f3565b92915050565b600060208284031215610135576101346100bc565b5b60006101438482850161010a565b91505092915050565b600082825260208201905092915050565b7f4661696c656420746f2073656e64204574686572000000000000000000000000600082015250565b600061019360148361014c565b915061019e8261015d565b602082019050919050565b600060208201905081810360008301526101c281610186565b905091905056fea2646970667358221220563acd6f5b8ad06a3faf5c27fddd0ecbc198408b99290ce50d15c2cf7043694964736f6c63430008120033\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSimpleSendFromContractNoBalance");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSimpleSendFromContractWithValidation()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"code\":\"0x60806040526004361061001e5760003560e01c80634b64e49214610023575b600080fd5b61003d6004803603810190610038919061011f565b61003f565b005b60008173ffffffffffffffffffffffffffffffffffffffff166108fc349081150290604051600060405180830381858888f193505050509050806100b8576040517f08c379a00000000000000000000000000000000000000000000000000000000081526004016100af906101a9565b60405180910390fd5b5050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100ec826100c1565b9050919050565b6100fc816100e1565b811461010757600080fd5b50565b600081359050610119816100f3565b92915050565b600060208284031215610135576101346100bc565b5b60006101438482850161010a565b91505092915050565b600082825260208201905092915050565b7f4661696c656420746f2073656e64204574686572000000000000000000000000600082015250565b600061019360148361014c565b915061019e8261015d565b602082019050919050565b600060208201905081810360008301526101c281610186565b905091905056fea2646970667358221220563acd6f5b8ad06a3faf5c27fddd0ecbc198408b99290ce50d15c2cf7043694964736f6c63430008120033\",\"balance\":\"0x3e8\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"traceTransfers\":true,\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSimpleSendFromContractWithValidation");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSimpleSendFromContract()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"code\":\"0x60806040526004361061001e5760003560e01c80634b64e49214610023575b600080fd5b61003d6004803603810190610038919061011f565b61003f565b005b60008173ffffffffffffffffffffffffffffffffffffffff166108fc349081150290604051600060405180830381858888f193505050509050806100b8576040517f08c379a00000000000000000000000000000000000000000000000000000000081526004016100af906101a9565b60405180910390fd5b5050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100ec826100c1565b9050919050565b6100fc816100e1565b811461010757600080fd5b50565b600081359050610119816100f3565b92915050565b600060208284031215610135576101346100bc565b5b60006101438482850161010a565b91505092915050565b600082825260208201905092915050565b7f4661696c656420746f2073656e64204574686572000000000000000000000000600082015250565b600061019360148361014c565b915061019e8261015d565b602082019050919050565b600060208201905081810360008301526101c281610186565b905091905056fea2646970667358221220563acd6f5b8ad06a3faf5c27fddd0ecbc198408b99290ce50d15c2cf7043694964736f6c63430008120033\",\"balance\":\"0x3e8\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}],\"traceTransfers\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSimpleSendFromContract");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallSimple()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x3e8\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallSimple");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallTransactionTooHighNonce()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x64\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallTransactionTooHighNonce");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallTransactionTooLowNonce38010()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"nonce\":\"0xa\"}},\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x0\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallTransactionTooLowNonce38010");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallTransferOverBlockStateCalls()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"balance\":\"0x7d0\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"value\":\"0x3e8\"},{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc300000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]},{\"stateOverrides\":{\"0xc300000000000000000000000000000000000000\":{\"balance\":\"0x0\"}},\"calls\":[{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"value\":\"0x3e8\"},{\"from\":\"0xc300000000000000000000000000000000000000\",\"to\":\"0xc200000000000000000000000000000000000000\",\"value\":\"0x3e8\"}]}]}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallTransferOverBlockStateCalls");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }

    [Test]
    public async Task TestmulticallTryToMoveNonPrecompile()
    {
        EthereumJsonSerializer serializer = new();
        string input = "{\"blockStateCalls\":[{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"nonce\":\"0x5\"}}},{\"stateOverrides\":{\"0xc000000000000000000000000000000000000000\":{\"MovePrecompileToAddress\":\"0xc100000000000000000000000000000000000000\"}},\"calls\":[{\"from\":\"0xc000000000000000000000000000000000000000\",\"to\":\"0xc000000000000000000000000000000000000000\",\"nonce\":\"0x0\"},{\"from\":\"0xc100000000000000000000000000000000000000\",\"to\":\"0xc100000000000000000000000000000000000000\",\"nonce\":\"0x5\"}]}],\"validation\":true}";
        var payload = serializer.Deserialize<MultiCallPayload<TransactionForRpc>>(input);
        TestRpcBlockchain chain = await EthRpcMulticallTestsBase.CreateChain();
        Console.WriteLine("current test: multicallTryToMoveNonPrecompile");
        var result = chain.EthRpcModule.eth_multicallV1(payload!, BlockParameter.Latest);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.IsNotNull(result.Data);
    }
}
