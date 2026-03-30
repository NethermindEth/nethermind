// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Test;

public class Eip8037RegressionTests : VirtualMachineTestsBase
{
    private static readonly TestSpecProvider Block8SpecProvider = new(Amsterdam.Instance)
    {
        ChainId = 0x301824,
        NetworkId = 0x301824,
    };
    private const long GethBlock8Number = 8;
    private const ulong GethBlock8Timestamp = 0x69c7a35c;
    private const long GethBlock8GasLimit = 0x3938700;
    private const ulong GethBlock8BaseFee = 0x3788359d;
    private const ulong GethBlock8BlobGasUsed = 0;
    private const ulong GethBlock8ExcessBlobGas = 0;
    private const ulong GethBlock8SlotNumber = 0x0f;
    private static readonly Address GethBlock8Miner = new("0x8943545177806ed17b9f23f0a21ee5948ecaa776");
    private static readonly Hash256 GethBlock8ParentHash = new("0xe11628b69dbcc4547f575e021e43dbff86b10ad67301d539285d1943f5b77bfc");
    private static readonly Hash256 GethBlock8ParentBeaconBlockRoot = new("0xe41085ab570510be3240011ab9a82ef32099f557044fc25523df0739991cfeae");
    private static readonly Hash256 GethBlock8MixHash = new("0xd531a2e574ba00e535caa8d2065b86e319632aca1bb22b43cd3c0b8000239d3e");
    private static readonly IReadOnlyDictionary<long, Hash256> GethBlockHashes = new Dictionary<long, Hash256>
    {
        [0] = new("0xcae4e57df9d32b67e6bbfbf90f3c93dd6809d9b62df2c61dd71f6a36e9540c8a"),
        [1] = new("0xdc9a7648dc6742df52ed402b99005b966eb0b56064d407f56fea9cecc580f95f"),
        [2] = new("0xc2189ba10ba395b3d5b02475495dcc345199f590e27609a9e6a13de48908177f"),
        [3] = new("0x3680fa3e0502910ff9175368dc608b906dee9b084d0e50ec0c2d6b232a00064c"),
        [4] = new("0xc8450f815b4e0723962b7466d0349dc5572503d1d46170f536861f61367d008c"),
        [5] = new("0x5b103313d9d4a0fb52da3220b74c9f916f924182e8dca2d7b733bea5305f59de"),
        [6] = new("0x23272c09cb747df501ed9a1260ff070ad7191e7117d503fb3ec6655c2fd3e8e2"),
        [7] = new("0xe11628b69dbcc4547f575e021e43dbff86b10ad67301d539285d1943f5b77bfc"),
        [8] = new("0xd9183eccfbf078b12a63f1dec24388758da0b36442492edb327106c07ffc257d"),
    };

    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
    protected override ISpecProvider SpecProvider => Block8SpecProvider;

    public static IEnumerable<TestCaseData> BadBlock8CreateTransactionCases()
    {
        yield return new TestCaseData(
            "0x02f90240833018248084773594008504a817c800830f42408082fc28b901e17f00000000000000000000000000000000000000000000000000000000000002007f000000000000000000000000000000000000000000000000000000000000003a60706202ffff16816202ffff1691508261ffff1692503760b66002673f9c8bad68dae6244262bb545762df682160eb61d6ad614626886a142583a9f0e9d7c70784fe6b170aba9d5c990188106e8b3533458d6826e8a843b9e72f1a9a487f7ad2c14f6486794f3ddeae588b4236e7408723b721c5fc981507abb1e83e73d26000527f3c744b75b901c5f248765f016e03d3b09e5a867320b04209eb88a63f51ab422f6020527f5c552c0a1ac03c2ee95df3fa2d1e3c0a1ee0efe0fec14e30ee5c3182da4b86f86040527f5424b69c752bf0fdbdac3e82d39234c14cdcbb722ec66085360e51fd312ff6f46060527fc1aa2f68706ae12686755c0966e966b9bc365a33adbb8341e8d4f0070addc8016080527fec0000000000000000000000000000000000000000000000000000000000000060a052602060e06100a16000600060035af160e05160e051816202ffff169150826202ffff1692508361ffff1693503c5b5b5b6202ffff165c83331e6101145743585b6202ffff16525b7fc3be23cfa2f860f9b3317ae15b20a2e7b1fb0ac6193533812afdff62233289391400c080a0d4199f2f0592bcdfd8b93521b096f37a5c9c3c7ada484fd8794990075193c687a0530f74b3d4a6e4d4e1c265bfcf14aa7ac78da74246871716457a5938a1248f47",
            "0xfee516fb0e93d96d08ebca5ad070da5008b11f8d",
            1_000_000L,
            StatusCode.Failure)
            .SetName("Amsterdam_bad_block_8_create_bad_jump_matches_geth_gas");

        yield return new TestCaseData(
            "0x02f90248833018248084773594008504a817c800830f42408082a9aeb901e97f00000000000000000000000000000000000000000000000000000000000002007f000000000000000000000000000000000000000000000000000000000000003b613c9f5b5b587f00000000000000000000000000000000000000000000000000000000000000006000527f00000000000000000000000000000000000000000000000000000000000000006020527f568b1e399e3c660d790466cb5aa8205df7c5e04400ff428bfd736aaa42a748526040526040606060606000600060075af16060516080515b326202ffff165c7f11d5f3dfdfdfab263dd4b5c12033c6ca97cc642bea6372cce4ebafb60f300eca6000527f9fc4a741bf4a34d6902f7eb7b6229a8c59146abe58f3a917d610aaae3efde83b6020527fe9d62362482a82d2b3cab9ca13b59c39869b239954e633068f813d8e44b141776040527f40f1b4ecccbe49988a14257ef66b51000000000000000000000000000000000060605261006f60a061006f6000600060045af160a05160a05160c05160e051610100514360056613eca8b5fd49ba607f9a6ae17aafebc77e2f1276dc0b475b4771d8432700320ef928fe16e4e7547d9d04ba255bf538805f5f397f63000001e75679ba6767deee00e85bb3b2c42d18e8338d64787d64d411ec54355f525f61a748f05f5f5f5f5f855af15b00c001a01e8250c52853496fa274ac73f77599c161df70576b0b102260dcccfdd72604efa0765c2b7b2a383a6b26e6197329ec968e0fd7a20e5e699825f42d04c291bc798e",
            "0xf6a2a72ce31810210754b0b08cc13301e2ba3013",
            488_588L,
            StatusCode.Success)
            .SetName("Amsterdam_bad_block_8_create_success_matches_geth_gas");

        yield return new TestCaseData(
            "0x02f901fd833018248084773594008504a817c800830f42408082a895b9019e7f00000000000000000000000000000000000000000000000000000000000002007f000000000000000000000000000000000000000000000000000000000000003f463259608660f3612d01786ea401da60fbbbff2b48ed2df8a5f0a88a440ad89ddfcfba5197956202ffff168161ffff169150fd5b7f00000000000000000000000000000000000000000000000000000000000000016000527f00000000000000000000000000000000000000000000000000000000000000026020527f00000000000000000000000000000000000000000000000000000000000000016040527f00000000000000000000000000000000000000000000000000000000000000026060527f00000000000000000000000000000000000000000000000000000000000000016080527f000000000000000000000000000000000000000000000000000000000000000260a052602060e060c06000600060085af160e051e8361d5b5b6202ffff168161ffff16915020685e5bf95f2518b5c297608060b460b4620570e330714314eb48a75abb90b0a0dc9e21653ba7c2659b8a5b5b00c001a0d04f162175b54ec50bc1dd724b5ff112749d0ae2606f54fd0e132ef9e82874b7a0411f7a01b46d79942532507123258156e8c942b8ef24cb25cf39bf0b87efe9ca",
            "0x2c6816646a90a441a98fb39e065565fc6dd844f8",
            167_425L,
            StatusCode.Failure)
            .SetName("Amsterdam_bad_block_8_create_revert_matches_geth_gas");

        yield return new TestCaseData(
            "0x02f90240833018248084773594008504a817c800830f42408082b9d0b901e17f00000000000000000000000000000000000000000000000000000000000002007f00000000000000000000000000000000000000000000000000000000000000480a7fdac1b1e89b690603bf57fb5968ef3d6f58dffed37f795060c200ba7b636f61d06000527f63551989d9ed56317f7eb91a9fff274f2db39def64fb684efdd0612c695f3bc66020527fec6b580000000000000000000000000000000000000000000000000000000000604052602060806100436000600060025af160805160805110595b616a296202ffff16816202ffff1691508261ffff169250394b61e35c456202ffff16816202ffff1691508261ffff1692503732600f446202ffff16816202ffff1691508261ffff16925037700d6b4458557f1b83da967b4a4466eac1cf63651a6311645b4ff2fb2260a86202ffff168161ffff169150a265d894309de72bff62dac0a11e36325b5b30463d6202ffff168161ffff169150a4627247f6674ba002611e04a6aa639310398c647cefbd3d1a4260ee94634e0287e66f75ba591a232c2f5897810f6df11f8d5a7b5e3ca7aa9c0f4e624b9576fad6713de82166e138902852f3c50c5e5e363a8161ffff169150836202ffff1693508461ffff169450856202ffff1695508661ffff169650f264fcadd77f8a345bf05b00c001a0a5501d8b86a0127ed0e66f1e64176b37b66aa8476f73772812effeccbb24fef3a01c707616bd1009fb75ca8f1e39a7ad612e38d871ce6461041e015bbddb0994c0",
            "0x5aacf013614abe024e7bdb6acaf6cc4aadbd938b",
            929_686L,
            StatusCode.Success)
            .SetName("Amsterdam_bad_block_8_create_large_success_matches_geth_gas");

        yield return new TestCaseData(
            "0x02f9025d833018248084773594008504a817c800830f42408082cbaeb901fe7f00000000000000000000000000000000000000000000000000000000000002007f000000000000000000000000000000000000000000000000000000000000004965c4ce261272196202ffff16546202ffff16553665d5d227614241406202ffff16514a7f000000000000000000000000000000000cbafd9fa656b12082e2cb00abb28f236000527fb8131bc6f73a32f3ba04cb598e8b48c4636eb977d0e6b37c8f4fa50539d6fbb86020527f000000000000000000000000000000000b391b8125669f58ef7d91a5e058f7b06040527fa1bc4c120d709f95d876fc2861a1db8dba7ddac5cc0b694299efec73a6fe4cd4606052610100608060806000600060115af160805160a05160c05160e051610100516101205161014051610160515b5b86343a466202ffff168161ffff169150a178c554fe68388bbf63ba12223403baf0da0686fc7bf460aa18cb455b136202ffff168161ffff16915020603b45603f161a465b6202ffff168161ffff169150a37cce379dba10dd066ca5f97b9d8f7ea72c71d34f357a6de8df60e44516f54b328c5b589d46130a066202ffff165d326202ffff165c186b92cf84672c6c9a863423d52569b0b3333183edf4da74a23d5b38805f5f397f63000001fc5679ac18290c4682f4452a81404264d68dc335eea81c4a7f2bf2ce5f52636cafadb6905f5ff55f5f5f5f5f855af15b00c001a0329448fd0e890f9ec33ea9a54efe298721e5cd37c9664672178a6d8e2891c6c2a0155de24e1267b7a1ff859937a0bc53d85efe0b9a30cbc404e55d6b16fefb808f",
            "0x35f3567cff6c0f60acf433af8a7cd44fbcdde173",
            818_464L,
            StatusCode.Success)
            .SetName("Amsterdam_bad_block_8_create_followup_success_matches_geth_gas");
    }

    [TestCase(false, TestName = "Eip8037_failed_nested_CREATE_balance_check_burns_tx_gas_but_excludes_create_state_from_block_gas")]
    [TestCase(true, TestName = "Eip8037_failed_nested_CREATE2_balance_check_burns_tx_gas_but_excludes_create_state_from_block_gas")]
    public void Eip8037_failed_nested_create_balance_check_must_not_reduce_block_gas(bool create2)
    {
        UInt256 impossibleValue = 101.Ether;
        byte[] childInitCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        Prepare prepare = Prepare.EvmCode;
        prepare = create2
            ? prepare.Create2(childInitCode, [0x01], impossibleValue)
            : prepare.Create(childInitCode, impossibleValue);

        byte[] code = prepare
            .Op(Instruction.POP)
            .Op(Instruction.ADD)
            .Done;

        const long gasLimit = 500_000;
        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            code,
            value: 0,
            blockGasLimit: gasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = false;
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(transaction.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(transaction.BlockGasUsed, Is.EqualTo(gasLimit - GasCostOf.CreateState));
        Assert.That(block.Header.GasUsed, Is.EqualTo(gasLimit - GasCostOf.CreateState));
    }

    [Test]
    public void Eip8037_failed_new_account_call_balance_check_burns_tx_gas_but_excludes_new_account_state_from_block_gas()
    {
        UInt256 impossibleValue = 101.Ether;
        byte[] code = Prepare.EvmCode
            .CallWithValue(TestItem.AddressC, 100_000, impossibleValue)
            .Op(Instruction.POP)
            .Op(Instruction.ADD)
            .Done;

        const long gasLimit = 300_000;
        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            code,
            value: 0,
            blockGasLimit: gasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = false;
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(transaction.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(transaction.BlockGasUsed, Is.EqualTo(gasLimit - GasCostOf.NewAccountState));
        Assert.That(block.Header.GasUsed, Is.EqualTo(gasLimit - GasCostOf.NewAccountState));
    }

    [Test]
    public void Eip8037_thrown_stack_overflow_must_preserve_prior_nested_create_state_gas()
    {
        UInt256 impossibleValue = 101.Ether;
        byte[] childInitCode = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        List<byte> code = new(
            Prepare.EvmCode
                .Create2(childInitCode, [0x01], impossibleValue)
                .Done);

        for (int i = 0; i < EvmStack.MaxStackSize; i++)
        {
            code.Add((byte)Instruction.PUSH0);
        }

        const long gasLimit = 300_000;
        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            code.ToArray(),
            value: 0,
            blockGasLimit: gasLimit);

        GasCaptureTracer tracer = new() { IsTracingAccess = false };
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.StackOverflow)));
        Assert.That(transaction.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(transaction.BlockGasUsed, Is.EqualTo(gasLimit - GasCostOf.CreateState));
        Assert.That(tracer.GasConsumed.BlockStateGas, Is.EqualTo(GasCostOf.CreateState));
        Assert.That(block.Header.GasUsed, Is.EqualTo(gasLimit - GasCostOf.CreateState));
    }

    [TestCaseSource(nameof(BadBlock8CreateTransactionCases))]
    public void Amsterdam_bad_block_8_create_transactions_match_geth_receipts(
        string rawTransaction,
        string expectedSender,
        long expectedSpentGas,
        byte expectedStatusCode)
    {
        Transaction transaction = Rlp.Decode<Transaction>(Bytes.FromHexString(rawTransaction), RlpBehaviors.SkipTypedWrapping);
        transaction.SenderAddress = new EthereumEcdsa(transaction.ChainId ?? SpecProvider.ChainId).RecoverAddress(transaction);

        Assert.That(transaction.SenderAddress, Is.EqualTo(new Address(expectedSender)));

        TestState.CreateAccount(transaction.SenderAddress!, 100.Ether);
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);

        TestAllTracerWithOutput tracer = CreateTracer();
        tracer.IsTracingAccess = false;
        (_, TransactionResult result) = ExecuteWithGethBlock8Environment(transaction, tracer);

        Assert.That(result, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.StatusCode, Is.EqualTo(expectedStatusCode));
        Assert.That(transaction.SpentGas, Is.EqualTo(expectedSpentGas));
    }

    /// <summary>
    /// When a nested CREATE's child frame has too little regular gas to cover both
    /// the regular code deposit cost AND the state-gas spill, the CREATE must fail.
    ///
    /// Gas budget (1-byte deployed contract, child state reservoir = 0):
    ///   regularDepositCost  = 6  (CodeDepositRegularPerWord × 1 word)
    ///   stateDepositCost    = 1174 (CostPerStateByte × 1 byte)
    ///   stateSpill          = 1174 (entire stateDepositCost spills into regular gas)
    ///   total regular needed = 6 + 1174 = 1180
    ///
    /// Child ends with 1175 regular gas after init code — 5 short.
    /// Without the fix, the pre-check passes (1175 ≥ 6 and 1175 ≥ 1174) and the
    /// charge runs on the merged parent+child pool, silently borrowing parent gas.
    /// </summary>
    [Test]
    public void Eip8037_nested_create_code_deposit_must_not_borrow_parent_regular_gas()
    {
        // Init code: deploys 1 byte of zeros from memory
        // PUSH1 1, PUSH1 0, RETURN = 5 bytes, costs 9 gas (3+3+3 memory expansion)
        byte[] initCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        // Factory code: CREATE(value=0, initCode), then RETURN the result (address or 0)
        byte[] factoryCode = Prepare.EvmCode
            .Create(initCode, UInt256.Zero)
            // Stack: [address or 0]
            .PushData(0)
            .Op(Instruction.MSTORE)   // store result at memory[0]
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)   // return 32 bytes
            .Done;

        // Gas calculation:
        //   Intrinsic (CALL to existing account): 21000
        //   Factory pre-CREATE opcodes: 21 gas
        //   CREATE opcode costs:
        //     CreateRegular(9000) + InitCodeWord(2) = 9002 regular
        //     CreateState(131488) → spills entirely to regular (factory has 0 state reservoir)
        //     Total: 140490 regular
        //   Remaining after CREATE costs: 1202
        //   63/64 rule: callGas = 1202 - floor(1202/64) = 1184, factory retains 18
        //   Child: 1184 gas → 9 for init code → 1175 remaining for code deposit
        //   Factory post-CREATE: 12 gas (PUSH, MSTORE, PUSH, PUSH, RETURN)
        //   Total: 21000 + 21 + 140490 + 1202 = 162713
        long gasLimit = 162713;

        TestAllTracerWithOutput tracer = Execute(Activation, gasLimit, factoryCode);

        // Transaction succeeds (factory runs fine), but the nested CREATE must fail
        // because the child can't afford the code deposit from its own gas alone.
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "Factory execution should succeed");

        // CREATE result: 0 = failure (returned in the 32-byte output)
        byte[] returnData = tracer.ReturnValue;
        Assert.That(returnData.IsZero(), Is.True,
            "Nested CREATE should fail: child has 1175 gas but needs 1180 for code deposit (6 regular + 1174 state spill)");
    }

    /// <summary>
    /// A child CALL that runs out of gas during SSTORE must not spill state gas into the
    /// parent frame's reservoir. If it does, the parent can incorrectly complete its own
    /// SSTORE with gas that should have been burned by the child halt.
    /// </summary>
    [Test]
    public void Eip8037_failed_child_sstore_must_not_inflate_parent_state_reservoir()
    {
        byte[] childCode = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);

        byte[] parentCode = Prepare.EvmCode
            .Call(TestItem.AddressC, 40_000)
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput tracer = Execute(Activation, 70_000, parentCode);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure),
            "The parent SSTORE should run out of gas once the child CALL burns its own failed SSTORE gas.");
        Assert.That(tracer.Error, Is.EqualTo("OutOfGas"));
    }

    private (Block Block, TransactionResult Result) ExecuteWithGethBlock8Environment(Transaction transaction, ITxTracer tracer)
    {
        IBlockhashProvider blockhashProvider = new HistoricalBlockhashProvider(GethBlockHashes);
        EthereumVirtualMachine machine = new(blockhashProvider, SpecProvider, GetLogManager());
        ITransactionProcessor processor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            SpecProvider,
            TestState,
            machine,
            CodeInfoRepository,
            GetLogManager());

        Block block = Build.A.Block
            .WithNumber(GethBlock8Number)
            .WithTimestamp(GethBlock8Timestamp)
            .WithTransactions(transaction)
            .WithGasLimit(GethBlock8GasLimit)
            .WithBaseFeePerGas(GethBlock8BaseFee)
            .WithBeneficiary(GethBlock8Miner)
            .WithParentHash(GethBlock8ParentHash)
            .WithMixHash(GethBlock8MixHash)
            .WithBlobGasUsed(GethBlock8BlobGasUsed)
            .WithExcessBlobGas(GethBlock8ExcessBlobGas)
            .WithParentBeaconBlockRoot(GethBlock8ParentBeaconBlockRoot)
            .WithSlotNumber(GethBlock8SlotNumber)
            .WithPostMergeRules()
            .TestObject;

        TransactionResult result = processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        return (block, result);
    }

    private sealed class HistoricalBlockhashProvider(IReadOnlyDictionary<long, Hash256> hashes) : IBlockhashProvider
    {
        public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec) =>
            hashes.TryGetValue(number, out Hash256 hash) ? hash : null;

        public System.Threading.Tasks.Task Prefetch(BlockHeader currentBlock, System.Threading.CancellationToken token) =>
            System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class GasCaptureTracer : TestAllTracerWithOutput
    {
        public GasConsumed GasConsumed { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            GasConsumed = gasSpent;
            base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            GasConsumed = gasSpent;
            base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        }
    }
}
