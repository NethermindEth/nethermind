// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.SendPolicy.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class SendPolicyTxSenderTests
{
    private const ulong ChainId = TestBlockchainIds.ChainId;
    private static readonly EthereumEcdsa Ecdsa = new(ChainId);
    private static readonly PrivateKey Owner = TestItem.PrivateKeyA;
    private static readonly PrivateKey Stranger = TestItem.PrivateKeyB;
    private static readonly Address Friend = TestItem.AddressC;
    private static readonly Address Token = TestItem.AddressD;
    private static readonly Address Spender = TestItem.AddressE;
    private static readonly Address Unlisted = TestItem.AddressF;
    private static readonly Address Collection = Address.FromNumber(0x1155);
    private static readonly UInt256 OneEther = 1.Ether;

    private static readonly string[] Rules =
    [
        $"from  {Owner.Address}",
        $"to    {Friend} {OneEther}   # up to one ether per transaction",
        $"to    {Token}",
        $"grant {Spender}",
        "fee   10000000000000000",
        $"token {Collection}",
    ];

    private static readonly UInt256 FeeCeiling = 60_000 * 3.GWei;

    private static IEnumerable<TestCaseData> Sends()
    {
        yield return Case("stranger to an unlisted address", () => Send(Stranger, Unlisted, 1), null);
        yield return Case("owner to a listed address within its limit", () => Send(Owner, Friend, OneEther / 2), null);
        yield return Case("owner over the per-transaction limit", () => Send(Owner, Friend, 2 * OneEther), $"over the limit {OneEther} of rule line 2");
        yield return Case("owner to an unlisted address", () => Send(Owner, Unlisted, 1), $"send policy, unlisted destination {Unlisted}, add 'to {Unlisted}'");
        yield return Case("owner legacy transaction to an unlisted address", () => Send(Owner, Unlisted, 1, type: TxType.Legacy), $"unlisted destination {Unlisted}");
        yield return Case("owner with the sender not yet recovered", () => Unresolved(Send(Owner, Unlisted, 1)), $"unlisted destination {Unlisted}");
        yield return Case("owner contract creation", () => Send(Owner, null, 0), "contract creation is not allowed");
        yield return Case("owner blob transaction", () => Blob(Owner, Friend), "blob transaction is not allowed");
        yield return Case("owner ether to a destination without a limit", () => Send(Owner, Token, 1), "value 1 wei is over the limit 0 of rule line 3");
        yield return Case("owner fee ceiling over the fee rule", () => Send(Owner, Friend, 1, gasLimit: 10_000_000), "fee ceiling 30000000000000000 wei is over 'fee 10000000000000000'");
        yield return Case("owner cancellation by a zero self-send", () => Send(Owner, Owner.Address, 0), null);
        yield return Case("owner approve to a granted spender", () => Call(Owner, Token, "095ea7b3", Spender, UInt256.MaxValue), null);
        yield return Case("owner approve to an unlisted spender", () => Call(Owner, Token, "095ea7b3", Unlisted, UInt256.MaxValue), $"{Unlisted} may not receive tokens or an allowance, add 'grant {Unlisted}'");
        yield return Case("owner approve of zero to an unlisted spender", () => Call(Owner, Token, "095ea7b3", Unlisted, UInt256.Zero), $"{Unlisted} may not receive");
        yield return Case("owner revoke by an approve to the zero address", () => Call(Owner, Token, "095ea7b3", Address.Zero, 7), null);
        yield return Case("owner transfer to the zero address", () => Call(Owner, Token, "a9059cbb", Address.Zero, 5), $"{Address.Zero} may not receive");
        yield return Case("owner setApprovalForAll to an unlisted operator", () => Call(Owner, Token, "a22cb465", Unlisted, UInt256.One), $"{Unlisted} may not receive");
        yield return Case("owner revoke by setApprovalForAll false", () => Call(Owner, Token, "a22cb465", Unlisted, UInt256.Zero), null);
        yield return Case("owner transfer to an unlisted recipient", () => Call(Owner, Token, "a9059cbb", Unlisted, 5), $"{Unlisted} may not receive");
        yield return Case("owner transferFrom to an unlisted recipient", () => Call(Owner, Token, "23b872dd", Owner.Address, Unlisted, 5), $"{Unlisted} may not receive");
        yield return Case("owner truncated approve calldata", () => SendData(Owner, Token, Bytes.FromHexString("095ea7b3" + new string('0', 64))), "short calldata for a token call");
        yield return Case("owner transfer at a token destination to a granted recipient", () => Call(Owner, Collection, "a9059cbb", Spender, 5), null);
        yield return Case("owner ERC-1155 transfer at a token destination to an unlisted recipient", () => Call(Owner, Collection, "f242432a", Owner.Address, Unlisted, 7, 1), $"{Unlisted} may not receive");
        yield return Case("owner unknown call at a token destination", () => Call(Owner, Collection, "d505accf", Spender, 5), $"call 0xd505accf is not allowed at 'token {Collection}'");
        yield return Case("owner empty call at a token destination", () => Send(Owner, Collection, 0), $"only token calls are allowed at 'token {Collection}'");
        yield return Case("owner ether to a token destination", () => Send(Owner, Collection, 1), "value 1 wei is over the limit 0 of rule line 6");
        yield return Case("owner fee ceiling that overflows", () => Send(Owner, Friend, 1, maxFeePerGas: UInt256.MaxValue), "fee ceiling");
        yield return Case("stranger carrying the owner's delegation to unlisted code", () => Delegate(Stranger, Owner, Unlisted), $"delegation of {Owner.Address} to {Unlisted} needs 'grant {Unlisted}'");
        yield return Case("owner delegating itself to unlisted code", () => Delegate(Owner, Owner, Unlisted), $"delegation of {Owner.Address} to {Unlisted}");
        yield return Case("stranger carrying the owner's delegation to granted code", () => Delegate(Stranger, Owner, Spender), null);
        yield return Case("stranger delegating itself", () => Delegate(Stranger, Stranger, Unlisted), null);
        yield return Case("stranger carrying the owner's delegation to the zero address", () => Delegate(Stranger, Owner, Address.Zero), null);
        yield return Case("owner frame transaction", () => Build.A.Transaction.WithType(TxType.FrameTx).WithSenderAddress(Owner.Address).TestObject, "frame transaction is not allowed");
    }

    [TestCaseSource(nameof(Sends))]
    public async Task Send_is_forwarded_or_refused_by_the_rules(Func<Transaction> build, string? expectedRefusal)
    {
        using TempPath rules = WriteRules(Rules);
        ITxSender inner = AcceptingSender();
        Transaction tx = build();

        (Hash256 _, AcceptTxResult? result) = await CreateSender(inner, RulesFile(rules)).SendTransaction(tx, TxHandlingOptions.PersistentBroadcast);

        if (expectedRefusal is null)
        {
            await inner.Received(1).SendTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        }
        else
        {
            await inner.DidNotReceiveWithAnyArgs().SendTransaction(default!, default);
            Assert.That(result?.ToString(), Does.Contain(expectedRefusal));
        }
    }

    [Test]
    public async Task Warn_only_forwards_a_refused_send()
    {
        using TempPath rules = WriteRules(Rules);
        ITxSender inner = AcceptingSender();
        Transaction tx = Send(Owner, Unlisted, 1);

        (Hash256 _, AcceptTxResult? result) = await CreateSender(inner, RulesFile(rules), warnOnly: true).SendTransaction(tx, TxHandlingOptions.None);

        await inner.Received(1).SendTransaction(tx, TxHandlingOptions.None);
        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    private static IEnumerable<TestCaseData> SendsUnderUnusableRules()
    {
        yield return Case("owner to a listed address", () => Send(Owner, Friend, 1), "rule file unusable (line 7: unknown rule 'bogus' with 0 argument(s))");
        yield return Case("stranger carrying the owner's delegation to granted code", () => Delegate(Stranger, Owner, Spender), "rule file unusable");
        yield return Case("stranger to an unlisted address", () => Send(Stranger, Unlisted, 1), null);
    }

    [TestCaseSource(nameof(SendsUnderUnusableRules))]
    public async Task Unusable_rule_file_refuses_guarded_accounts_with_the_file_error(Func<Transaction> build, string? expectedRefusal)
    {
        using TempPath rules = WriteRules(Rules);
        ITxSender inner = AcceptingSender();
        SendPolicyTxSender sender = CreateSender(inner, RulesFile(rules));
        await sender.SendTransaction(Send(Owner, Friend, 1), TxHandlingOptions.None);
        File.AppendAllLines(RulesFile(rules), ["bogus"]);
        inner.ClearReceivedCalls();

        (Hash256 _, AcceptTxResult? result) = await sender.SendTransaction(build(), TxHandlingOptions.None);

        if (expectedRefusal is null) Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        else Assert.That(result?.ToString(), Does.Contain(expectedRefusal));
    }

    [Test]
    public async Task Edited_rule_file_applies_without_restart()
    {
        using TempPath rules = WriteRules(Rules);
        ITxSender inner = AcceptingSender();
        SendPolicyTxSender sender = CreateSender(inner, RulesFile(rules));
        (Hash256 _, AcceptTxResult? before) = await sender.SendTransaction(Send(Owner, Unlisted, 1), TxHandlingOptions.None);

        File.AppendAllLines(RulesFile(rules), [$"to {Unlisted} 1"]);
        (Hash256 _, AcceptTxResult? after) = await sender.SendTransaction(Send(Owner, Unlisted, 1), TxHandlingOptions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before?.ToString(), Does.Contain("unlisted destination"));
            Assert.That(after, Is.EqualTo(AcceptTxResult.Accepted));
        }
    }

    private static IEnumerable<TestCaseData> SendsUnderCap()
    {
        yield return CapCase("third send in the period", true, (0, 0, true), (1, 0, true), (2, 0, true));
        yield return CapCase("third send after the period", false, (0, 0, true), (1, 0, true), (2, 3601, true));
        yield return CapCase("replacement of a counted nonce", false, (0, 0, true), (0, 0, true), (1, 0, true));
        yield return CapCase("send after one the pool rejected", false, (0, 0, false), (0, 0, true), (1, 0, true));
    }

    private static TestCaseData CapCase(string name, bool lastIsRefused, params (ulong Nonce, int SecondsLater, bool PoolAccepts)[] sends) =>
        new TestCaseData(sends, lastIsRefused).SetName(name);

    [TestCaseSource(nameof(SendsUnderCap))]
    public async Task Cap_counts_value_and_fee_ceiling_of_accepted_sends_in_the_period((ulong Nonce, int SecondsLater, bool PoolAccepts)[] sends, bool lastIsRefused)
    {
        using TempPath rules = WriteRules([.. Rules, $"cap {OneEther + 2 * FeeCeiling} 3600"]);
        ITxSender inner = Substitute.For<ITxSender>();
        ManualTimestamper clock = new();
        SendPolicyTxSender sender = CreateSender(inner, RulesFile(rules), clock: clock);
        AcceptTxResult? last = null;

        foreach ((ulong nonce, int secondsLater, bool poolAccepts) in sends)
        {
            clock.UtcNow += TimeSpan.FromSeconds(secondsLater);
            inner.SendTransaction(default!, default).ReturnsForAnyArgs(new ValueTask<(Hash256, AcceptTxResult?)>((Keccak.Zero, poolAccepts ? AcceptTxResult.Accepted : AcceptTxResult.Invalid)));
            (_, last) = await sender.SendTransaction(Send(Owner, Friend, OneEther / 2, nonce: nonce), TxHandlingOptions.None);
        }

        if (!lastIsRefused) Assert.That(last, Is.EqualTo(AcceptTxResult.Accepted));
        else Assert.That(last?.ToString(), Does.StartWith($"send policy, over 'cap {OneEther + 2 * FeeCeiling} 3600': {3 * (OneEther / 2 + FeeCeiling)} wei"));
    }

    [Test]
    public async Task Cap_counts_sends_journaled_before_a_restart()
    {
        using TempPath rules = WriteRules([.. Rules, $"cap {OneEther + 2 * FeeCeiling} 3600"]);
        ITxSender inner = AcceptingSender();
        ManualTimestamper clock = new();
        SendPolicyTxSender beforeRestart = CreateSender(inner, RulesFile(rules), clock: clock);
        await beforeRestart.SendTransaction(Send(Owner, Friend, OneEther / 2, nonce: 0), TxHandlingOptions.None);
        await beforeRestart.SendTransaction(Send(Owner, Friend, OneEther / 2, nonce: 1), TxHandlingOptions.None);

        (Hash256 _, AcceptTxResult? result) = await CreateSender(inner, RulesFile(rules), clock: clock).SendTransaction(Send(Owner, Friend, OneEther / 2, nonce: 2), TxHandlingOptions.None);

        Assert.That(result?.ToString(), Does.Contain("over 'cap"));
    }

    [Test]
    public async Task Cap_counts_sends_whose_nonce_the_node_assigns()
    {
        using TempPath rules = WriteRules([.. Rules, $"cap {OneEther + 2 * FeeCeiling} 3600"]);
        ITxSender inner = Substitute.For<ITxSender>();
        ulong nextNonce = 0;
        inner.SendTransaction(default!, default).ReturnsForAnyArgs(call =>
        {
            call.Arg<Transaction>().Nonce = nextNonce++;
            return new ValueTask<(Hash256, AcceptTxResult?)>((Keccak.Zero, AcceptTxResult.Accepted));
        });
        SendPolicyTxSender sender = CreateSender(inner, RulesFile(rules));
        AcceptTxResult? last = null;

        for (int i = 0; i < 3; i++) (_, last) = await sender.SendTransaction(Send(Owner, Friend, OneEther / 2), TxHandlingOptions.ManagedNonce);

        Assert.That(last?.ToString(), Does.Contain("over 'cap"));
    }

    [Test]
    public async Task Journal_drops_sends_older_than_the_period()
    {
        using TempPath rules = WriteRules([.. Rules, $"cap {OneEther} 3600"]);
        ManualTimestamper clock = new();
        SendPolicyTxSender sender = CreateSender(AcceptingSender(), RulesFile(rules), clock: clock);
        await sender.SendTransaction(Send(Owner, Friend, 1, nonce: 0), TxHandlingOptions.None);
        await sender.SendTransaction(Send(Owner, Friend, 1, nonce: 1), TxHandlingOptions.None);

        clock.UtcNow += TimeSpan.FromSeconds(3601);
        await sender.SendTransaction(Send(Owner, Friend, 1, nonce: 2), TxHandlingOptions.None);

        Assert.That(File.ReadAllLines(RulesFile(rules) + ".journal"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Journal_is_not_written_without_a_cap()
    {
        using TempPath rules = WriteRules(Rules);

        await CreateSender(AcceptingSender(), RulesFile(rules)).SendTransaction(Send(Owner, Friend, 1), TxHandlingOptions.None);

        Assert.That(File.Exists(RulesFile(rules) + ".journal"), Is.False);
    }

    [Test]
    public async Task Cap_refuses_when_the_journal_is_unusable()
    {
        using TempPath rules = WriteRules([.. Rules, $"cap {OneEther} 3600"]);
        string[] journal = [$"1 {Owner.Address} 0 1", "not a journal line"];
        File.WriteAllLines(RulesFile(rules) + ".journal", journal);

        (Hash256 _, AcceptTxResult? result) = await CreateSender(AcceptingSender(), RulesFile(rules)).SendTransaction(Send(Owner, Friend, 1), TxHandlingOptions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result?.ToString(), Does.Contain("journal unusable"));
            Assert.That(File.ReadAllLines(RulesFile(rules) + ".journal"), Is.EqualTo(journal));
        }
    }

    [TestCase("bogus 0x01")]
    [TestCase("cap 1")]
    [TestCase("cap 1 0")]
    [TestCase("from")]
    [TestCase("to 0x01 1 2")]
    [TestCase("fee -1")]
    [TestCase("from not-an-address")]
    public void Parse_rejects_a_malformed_line_and_names_it(string line) =>
        Assert.That(() => SendPolicyRules.Parse(["# header", line]), Throws.TypeOf<FormatException>().With.Message.StartsWith("line 2:"));

    [TestCase("fee 1", "fee 2")]
    [TestCase("cap 1 1", "cap 2 2")]
    [TestCase("to 0x00000000000000000000000000000000000000aa 5", "token 0x00000000000000000000000000000000000000aa")]
    [TestCase("token 0x00000000000000000000000000000000000000aa", "to 0x00000000000000000000000000000000000000aa 5")]
    public void Parse_rejects_a_second_rule_for_the_same_subject(string first, string second) =>
        Assert.That(() => SendPolicyRules.Parse([first, second]), Throws.TypeOf<FormatException>().With.Message.StartsWith("line 2:"));

    [Test]
    public void Rule_file_must_be_usable_at_startup([Values] bool exists)
    {
        using TempPath rules = WriteRules(["bogus"]);
        SendPolicyConfig config = new() { Enabled = true, RulesPath = exists ? RulesFile(rules) : RulesFile(rules) + ".missing" };

        Assert.That(() => new SendPolicyRuleFile(config, LimboLogs.Instance), Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains("rule file unusable"));
    }

    [Test]
    public void Rule_file_requires_a_path() =>
        Assert.That(() => new SendPolicyRuleFile(new SendPolicyConfig { Enabled = true }, LimboLogs.Instance), Throws.TypeOf<InvalidConfigurationException>());

    [Test]
    public void Plugin_enabled_follows_config([Values] bool enabled) =>
        Assert.That(new SendPolicyPlugin(new SendPolicyConfig { Enabled = enabled }).Enabled, Is.EqualTo(enabled));

    [Test]
    public void Module_decorates_the_node_tx_sender()
    {
        using TempPath rules = WriteRules(Rules);
        using IContainer container = BuildNode(RulesFile(rules));

        Assert.That(container.Resolve<ITxSender>(), Is.TypeOf<SendPolicyTxSender>());
    }

    [Test]
    public void Module_startup_step_fails_on_an_unusable_rule_file()
    {
        using TempPath rules = WriteRules(["bogus"]);
        using IContainer container = BuildNode(RulesFile(rules));
        Type step = container.Resolve<IEnumerable<StepInfo>>().Single().StepType;

        Assert.That(() => container.Resolve(step), Throws.TypeOf<DependencyResolutionException>().With.InnerException.InnerException.TypeOf<InvalidConfigurationException>());
    }

    private static IContainer BuildNode(string rulesPath) => new ContainerBuilder()
        .AddSingleton(AcceptingSender())
        .AddModule(new SendPolicyModule())
        .AddSingleton<ISendPolicyConfig>(new SendPolicyConfig { Enabled = true, RulesPath = rulesPath })
        .AddSingleton<IEthereumEcdsa>(Ecdsa)
        .AddSingleton<ITimestamper>(Timestamper.Default)
        .AddSingleton<ILogManager>(LimboLogs.Instance)
        .Build();

    private static TestCaseData Case(string name, Func<Transaction> build, string? expectedRefusal) =>
        new TestCaseData(build, expectedRefusal).SetName(name);

    private static TempPath WriteRules(IEnumerable<string> lines)
    {
        TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        File.WriteAllLines(RulesFile(directory), lines);
        return directory;
    }

    private static string RulesFile(TempPath directory) => Path.Combine(directory.Path, "rules");

    private static ITxSender AcceptingSender()
    {
        ITxSender sender = Substitute.For<ITxSender>();
        sender.SendTransaction(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>())
            .Returns(ci => new ValueTask<(Hash256, AcceptTxResult?)>((ci.Arg<Transaction>().Hash!, AcceptTxResult.Accepted)));
        return sender;
    }

    private static SendPolicyTxSender CreateSender(ITxSender inner, string rulesPath, bool warnOnly = false, ITimestamper? clock = null)
    {
        SendPolicyConfig config = new() { Enabled = true, RulesPath = rulesPath, WarnOnly = warnOnly };
        return new SendPolicyTxSender(inner, new SendPolicyRuleFile(config, LimboLogs.Instance), new SendPolicyJournal(config, clock ?? Timestamper.Default, LimboLogs.Instance), config, Ecdsa, LimboLogs.Instance);
    }

    private static Transaction Send(PrivateKey from, Address? to, UInt256 value, TxType type = TxType.EIP1559, ulong gasLimit = 60_000, ulong nonce = 0, UInt256? maxFeePerGas = null) =>
        Build.A.Transaction
            .WithType(type)
            .WithChainId(ChainId)
            .WithNonce(nonce)
            .To(to)
            .WithValue(value)
            .WithGasLimit(gasLimit)
            .WithGasPrice(maxFeePerGas ?? 3.GWei)
            .WithMaxFeePerGas(maxFeePerGas ?? 3.GWei)
            .SignedAndResolved(Ecdsa, from)
            .TestObject;

    private static Transaction SendData(PrivateKey from, Address to, byte[] data) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithChainId(ChainId)
            .To(to)
            .WithValue(0)
            .WithData(data)
            .WithGasLimit(60_000)
            .WithMaxFeePerGas(3.GWei)
            .SignedAndResolved(Ecdsa, from)
            .TestObject;

    private static Transaction Call(PrivateKey from, Address to, string selector, params object[] arguments) =>
        SendData(from, to, Bytes.FromHexString(selector + string.Concat(arguments.Select(Word))));

    private static string Word(object argument) => argument switch
    {
        Address address => address.ToString(false, false).PadLeft(64, '0'),
        UInt256 value => value.ToBigEndian().ToHexString(false),
        int value => value.ToString("x64"),
        _ => throw new ArgumentException(argument.GetType().Name)
    };

    private static Transaction Blob(PrivateKey from, Address to) =>
        Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithChainId(ChainId)
            .To(to)
            .WithValue(0)
            .WithGasLimit(60_000)
            .WithMaxFeePerGas(3.GWei)
            .SignedAndResolved(Ecdsa, from)
            .TestObject;

    private static Transaction Delegate(PrivateKey sender, PrivateKey authority, Address code) =>
        Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithChainId(ChainId)
            .To(Friend)
            .WithValue(0)
            .WithGasLimit(200_000)
            .WithMaxFeePerGas(3.GWei)
            .WithAuthorizationCode(Ecdsa.Sign(authority, ChainId, code, 0))
            .SignedAndResolved(Ecdsa, sender)
            .TestObject;

    private static Transaction Unresolved(Transaction tx)
    {
        tx.SenderAddress = null;
        return tx;
    }
}
