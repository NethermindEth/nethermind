// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core
{
    public readonly record struct Result(ResultType ResultType, string? Error = null)
    {
        public static Result Fail(string error) => new() { ResultType = ResultType.Failure, Error = error };
        public static Result Success { get; } = new() { ResultType = ResultType.Success };
        public static implicit operator bool(Result result) => result.ResultType == ResultType.Success;
        public static implicit operator Result(string? error) => error is null ? Success : Fail(error);
        // Required for short-circuit && operator
        public static bool operator true(Result result) => result.ResultType == ResultType.Success;
        public static bool operator false(Result result) => result.ResultType == ResultType.Failure;
        // This provides the short-circuit behavior
        public static Result operator &(Result left, Result right) => left ? right : left;  // If left fails, return left (short-circuit)
        public override string ToString() => ResultType == ResultType.Success ? "Success" : $"Failure: {Error}";
    }

    public readonly record struct Result<TData>(ResultType ResultType, TData? Data = default, string? Error = null)
    {
        public static Result<TData> Fail(string error, TData? data = default) => new() { ResultType = ResultType.Failure, Error = error, Data = data };
        public static Result<TData> Success(TData data) => new() { ResultType = ResultType.Success, Data = data };
        public static implicit operator bool(Result<TData> result) => result.IsSuccess;
        public static implicit operator Result<TData>(TData data) => Success(data);
        public static implicit operator Result<TData>(string error) => Fail(error, typeof(TData) == typeof(byte[]) ? (TData)(object)Array.Empty<byte>() : default);
        public static implicit operator (TData?, bool)(Result<TData> result) => (result.Data, result.IsSuccess);
        public static implicit operator Result(Result<TData> result) => result.Error;
        // Required for short-circuit && operator
        public static bool operator true(Result<TData> result) => result.IsSuccess;
        public static bool operator false(Result<TData> result) => result.IsError;
        public static explicit operator TData(Result<TData> result) =>
            result.Data ?? throw new InvalidOperationException($"Cannot convert {nameof(Result<>)} to TData when {nameof(result.ResultType)} is {nameof(ResultType.Failure)} with error: {result.Error}");

        // This provides the short-circuit behavior
        public static Result<TData> operator &(Result<TData> left, Result<TData> right) => left ? right : left;  // If left fails, return left (short-circuit)

        [MemberNotNullWhen(true, nameof(Data))]
        [MemberNotNullWhen(false, nameof(Error))]
        public bool IsSuccess => ResultType == ResultType.Success;

        [MemberNotNullWhen(false, nameof(Data))]
        [MemberNotNullWhen(true, nameof(Error))]
        public bool IsError => !IsSuccess;

        public void Deconstruct(out TData? result, out bool success)
        {
            result = Data;
            success = ResultType == ResultType.Success;
        }

        public bool Success([NotNullWhen(true)] out TData? result, [NotNullWhen(false)] out string? error)
        {
            result = Data;
            error = Error;
            return ResultType == ResultType.Success;
        }
    }
}
