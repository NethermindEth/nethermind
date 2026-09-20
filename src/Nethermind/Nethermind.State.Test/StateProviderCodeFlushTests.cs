// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture]
public class StateProviderCodeFlushTests
{
    [TestCase(false, TestName = "Commit_WhenCodeIsStaged_ReportsCodeWithoutFlushing")]
    [TestCase(true, TestName = "Commit_WhenCodeIsFlushed_ReportsCodeAfterFlush")]
    public void Commit_WhenTracingCodeChanges_PreservesCodeAcrossCommits(bool commitRoots)
    {
        RecordingCodeDb codeDb = new();
        StateProvider provider = CreateProvider(codeDb);
        IWorldStateTracer tracer = Substitute.For<IWorldStateTracer>();
        tracer.IsTracingState.Returns(true);

        foreach (Address address in new[] { TestItem.AddressA, TestItem.AddressB })
        {
            byte[] code = [0x60, (byte)codeDb.Writes, 0x00];
            ValueHash256 hash = ValueKeccak.Compute(code);
            provider.CreateAccount(address, 1);
            provider.InsertCode(address, hash, code, Prague.Instance);

            provider.Commit(Prague.Instance, tracer, commitRoots, false);

            tracer.Received(1).ReportCodeChange(address, null, Arg.Is<byte[]>(bytes => bytes.SequenceEqual(code)));
            Assert.That(provider.GetCode(hash), Is.EqualTo(code), "committed and staged code must remain readable");
        }
        Assert.That(codeDb.Writes, Is.EqualTo(commitRoots ? 2 : 0), "tracing must not force a staged-only commit to flush");
    }

    [Test]
    public void Commit_WhenCodeFlushFails_DoesNotReportUncommittedCode()
    {
        RecordingCodeDb codeDb = new() { Fail = true };
        StateProvider provider = CreateProvider(codeDb);
        IWorldStateTracer tracer = Substitute.For<IWorldStateTracer>();
        tracer.IsTracingState.Returns(true);
        byte[] code = [0x60, 0x01, 0x00];
        provider.CreateAccount(TestItem.AddressA, 1);
        provider.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, Prague.Instance);

        Assert.Throws<IOException>(() => provider.Commit(Prague.Instance, tracer, true, false));

        tracer.DidNotReceive().ReportCodeChange(Arg.Any<Address>(), Arg.Any<byte[]>(), Arg.Any<byte[]>());
    }

    private static StateProvider CreateProvider(RecordingCodeDb codeDb)
    {
        IWorldStateScopeProvider.IScope scope = Substitute.For<IWorldStateScopeProvider.IScope>();
        scope.CodeDb.Returns(codeDb);
        StateProvider provider = new(LimboLogs.Instance, new LocalMetrics());
        provider.SetScope(scope);
        return provider;
    }

    private sealed class RecordingCodeDb : IWorldStateScopeProvider.ICodeDb, IWorldStateScopeProvider.ICodeSetter
    {
        private byte[] _code;
        public int Writes { get; private set; }
        public bool Fail { get; init; }
        public byte[] GetCode(in ValueHash256 codeHash) => _code;
        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => this;
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
        {
            _code = code.ToArray();
            Writes++;
        }

        public void Dispose()
        {
            if (Fail) throw new IOException("code flush failed");
        }
    }
}
