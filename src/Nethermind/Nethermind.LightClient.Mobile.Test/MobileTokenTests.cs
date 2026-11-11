// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.LightClient.Mobile;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.LightClient.Test;

public class MobileTokenTests
{
    private static readonly Address Owner = new("0xde198901c5ee4611142e0c61dfbbcec7ab468f32");
    private static readonly Address Contract = new(MobileToken.Usdc.ContractAddress);
    private static readonly Address Implementation = new("0x1234567890123456789012345678901234567890");
    private static readonly byte[] BalanceOfCode =
    [
        0x60, 0x00, 0x35, 0x60, 0xe0, 0x1c, 0x63, 0x70, 0xa0, 0x82, 0x31, 0x14, 0x60, 0x14, 0x57,
        0x60, 0x00, 0x60, 0x00, 0xfd, 0x5b, 0x60, 0x04, 0x35, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3,
    ];

    [Test]
    public async Task Balance_of_executes_abi_and_proxy_storage_at_the_authenticated_head([Values] bool proxy,
        [Values(0UL, 1234567UL)] ulong units)
    {
        TokenSource source = new(BalanceOfCode, proxy) { Units = units };
        using VerifiedCall calls = new(source, new SingleReleaseSpecProvider(Osaka.Instance, 1, 1), NullLogManager.Instance);
        MobileTokenReader reader = new(calls, Owner, TimeProvider.System);
        MobileTokenBalance balance = await reader.ReadAsync(MobileToken.Usdc, source.Head, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(balance.Units, Is.EqualTo(units.ToString()));
            Assert.That(balance.Symbol, Is.EqualTo("USDC"));
            Assert.That(balance.BlockHash, Is.EqualTo(source.Head.BlockHash.ToString()));
            Assert.That(balance.StateRoot, Is.EqualTo(source.Head.StateRoot.ToString()));
            Assert.That(balance.BlockNumber, Is.EqualTo(source.Head.Number));
            Assert.That(balance.Error, Is.Null);
            Assert.That(balance.RequestMilliseconds, Is.GreaterThanOrEqualTo(0));
            Assert.That(source.StorageAddress, Is.EqualTo(Contract));
            Assert.That(source.StorageKey, Is.EqualTo(new UInt256(Owner.Bytes, isBigEndian: true)));
            Assert.That(source.CodeFetches, Is.EqualTo(proxy ? 2 : 1));
        }
        source.Units++;
        source.Head = new(101, 26_000_001, Keccak.Compute("next block"), Keccak.Compute("next state"));
        MobileTokenBalance updated = await reader.ReadAsync(MobileToken.Usdc, source.Head, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Units, Is.EqualTo((units + 1).ToString()));
            Assert.That(updated.BlockHash, Is.EqualTo(source.Head.BlockHash.ToString()));
        }
    }

    [Test]
    public async Task Missing_or_failed_calls_never_become_zero([Values("revert", "empty", "short", "missing-proof", "cancelled")] string failure)
    {
        byte[] code = failure switch
        {
            "revert" => [0x60, 0x00, 0x60, 0x00, 0xfd],
            "empty" => [0x00],
            "short" => [0x60, 0x01, 0x60, 0x00, 0xf3],
            _ => BalanceOfCode,
        };
        TokenSource source = new(code, false) { Unavailable = failure == "missing-proof" };
        using VerifiedCall calls = new(source, new SingleReleaseSpecProvider(Osaka.Instance, 1, 1), NullLogManager.Instance);
        MobileTokenReader reader = new(calls, Owner, TimeProvider.System);
        using CancellationTokenSource stop = new();
        if (failure == "cancelled") await stop.CancelAsync();
        Exception? error = await Assert.CatchAsync(async () => await reader.ReadAsync(MobileToken.Usdc, source.Head, stop.Token));
        Assert.That(error, Is.InstanceOf(failure switch
        {
            "revert" => typeof(RpcException),
            "cancelled" => typeof(OperationCanceledException),
            "missing-proof" => typeof(IOException),
            _ => typeof(InvalidDataException),
        }));
    }

    [Test]
    public async Task One_unavailable_token_does_not_prevent_another_verified_balance()
    {
        TokenSource source = new(BalanceOfCode, true) { Units = 123 };
        using VerifiedCall calls = new(source, new SingleReleaseSpecProvider(Osaka.Instance, 1, 1), NullLogManager.Instance);
        MobileTokenReader reader = new(calls, Owner, TimeProvider.System);
        Task<MobileTokenBalance> unavailable = reader.ReadAsync(MobileToken.Usdt, source.Head, CancellationToken.None);
        Task<MobileTokenBalance> available = reader.ReadAsync(MobileToken.Usdc, source.Head, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await unavailable);
        Assert.That((await available).Units, Is.EqualTo("123"));
    }

    [Test]
    public async Task Token_reads_refetch_evicted_state_without_reusing_another_heads_balance(
        [Values(8192L, VerifiedMemoryCache.DefaultCapacity)] long capacity)
    {
        TokenSource source = new(BalanceOfCode, true);
        using VerifiedCall calls = new(source, new SingleReleaseSpecProvider(Osaka.Instance, 1, 1), NullLogManager.Instance,
            cacheCapacity: capacity);
        MobileTokenReader reader = new(calls, Owner, TimeProvider.System);
        VerifiedHead first = source.Head;
        for (ulong i = 0; i < 8; i++)
        {
            source.Head = i == 0 ? first : new(100 + i, 26_000_000 + i, Keccak.Compute($"block {i}"), Keccak.Compute($"state {i}"));
            source.Units = 100 + i;
            Assert.That((await reader.ReadAsync(MobileToken.Usdc, source.Head, CancellationToken.None)).Units,
                Is.EqualTo((100 + i).ToString()));
        }
        Assert.That(source.HeaderFetches, Is.EqualTo(8));
        source.Head = first;
        source.Units = 100;
        MobileTokenBalance revisited = await reader.ReadAsync(MobileToken.Usdc, first, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(revisited.Units, Is.EqualTo("100"));
            Assert.That(revisited.BlockHash, Is.EqualTo(first.BlockHash.ToString()));
            Assert.That(source.HeaderFetches, Is.EqualTo(capacity == 8192 ? 9 : 8));
        }
    }

    private sealed class TokenSource : IExecutionStateSource
    {
        private readonly Dictionary<Address, byte[]> _code;
        internal VerifiedHead Head { get; set; } = new(100, 26_000_000, Keccak.Compute("block"), Keccak.Compute("state"));
        internal UInt256 Units { get; set; }
        internal bool Unavailable { get; init; }
        internal int CodeFetches { get; private set; }
        internal int HeaderFetches { get; private set; }
        internal Address? StorageAddress { get; private set; }
        internal UInt256 StorageKey { get; private set; }

        internal TokenSource(byte[] code, bool proxy) =>
            _code = proxy ? new()
            {
                [Contract] = Convert.FromHexString("363d3d373d3d3d363d73" + Implementation.ToString(withZeroX: false, withEip55Checksum: false) + "5af43d82803e903d91602b57fd5bf3"),
                [Implementation] = code,
            } : new() { [Contract] = code };

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            Assert.That(head, Is.EqualTo(Head));
            return Task.FromResult(_code.TryGetValue(address, out byte[]? code)
                ? new Account(1, 0, head.StateRoot, Keccak.Compute(code)) : Account.TotallyEmpty);
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
        {
            Assert.That(head, Is.EqualTo(Head));
            if (Unavailable) throw new ExecutionDataUnavailableException("No verified storage supplier.");
            StorageAddress = address;
            StorageKey = key;
            return Task.FromResult(Units);
        }

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
        {
            CodeFetches++;
            return Task.FromResult(_code.Values.Single(code => Keccak.Compute(code) == account.CodeHash));
        }

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            HeaderFetches++;
            return Task.FromResult(new BlockHeader(Keccak.Compute("parent"), Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
                head.Number, 30_000_000, 1_790_000_000, [])
            {
                Hash = head.BlockHash,
                StateRoot = head.StateRoot,
                MixHash = Keccak.Compute("random"),
                BaseFeePerGas = 1,
                BlobGasUsed = 0,
                ExcessBlobGas = 0,
                IsPostMerge = true,
            });
        }

        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
