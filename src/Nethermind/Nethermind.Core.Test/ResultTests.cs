// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ResultTests
{
    [Test]
    public void Default_is_success()
    {
        using IDisposable _ = Assert.EnterMultipleScope();
        Assert.That(default(Result).ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(default(Result), Is.EqualTo(Result.Success));
        Assert.That(default(Result<byte[]>).IsSuccess, Is.True);
    }

    [Test]
    public void Fail_with_null_error_throws()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Fail(null!));
        Assert.Throws<ArgumentNullException>(() => Result<int>.Fail(null!));
    }

    [Test]
    public void Failure_keeps_error_and_data()
    {
        Result<int> result = Result<int>.Fail("error", 5);

        using IDisposable _ = Assert.EnterMultipleScope();
        Assert.That(result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.Error, Is.EqualTo("error"));
        Assert.That(result.Data, Is.EqualTo(5));
    }

    [Test]
    public void Implicit_error_conversion_sets_result_type([Values(null, "error")] string? error)
    {
        Result result = error;
        Assert.That(result.ResultType, Is.EqualTo(error is null ? ResultType.Success : ResultType.Failure));
    }

    [Test]
    public void Implicit_error_conversion_to_bytes_result_fails_with_empty_data()
    {
        Result<byte[]> result = "error";

        using IDisposable _ = Assert.EnterMultipleScope();
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Data, Is.Empty);
    }

    [Test]
    public void Size_is_payload_only()
    {
        using IDisposable _ = Assert.EnterMultipleScope();
        Assert.That(Unsafe.SizeOf<Result>(), Is.EqualTo(IntPtr.Size));
        Assert.That(Unsafe.SizeOf<Result<byte[]>>(), Is.EqualTo(2 * IntPtr.Size));
    }
}
