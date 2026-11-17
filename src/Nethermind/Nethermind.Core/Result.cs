// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core
{
    /// <remarks>A null <see cref="Error"/> means success, so <c>default</c> is <see cref="Success"/>.</remarks>
    public readonly record struct Result
    {
        private Result(string? error) => Error = error;

        public string? Error { get; }
        public ResultType ResultType => Error is null ? ResultType.Success : ResultType.Failure;

        public static Result Fail(string error)
        {
            ArgumentNullException.ThrowIfNull(error);
            return new(error);
        }

        public static Result Success { get; } = default;
        public static implicit operator bool(Result result) => result.ResultType == ResultType.Success;
        public static implicit operator Result(string? error) => error is null ? Success : Fail(error);
        // Required for short-circuit && operator
        public static bool operator true(Result result) => result.ResultType == ResultType.Success;
        public static bool operator false(Result result) => result.ResultType == ResultType.Failure;
        // This provides the short-circuit behavior
        public static Result operator &(Result left, Result right) => left ? right : left;  // If left fails, return left (short-circuit)
        public override string ToString() => ResultType == ResultType.Success ? "Success" : $"Failure: {Error}";
    }

    /// <remarks>A null <see cref="Error"/> means success, so <c>default</c> is a success with default <see cref="Data"/>.</remarks>
    public readonly record struct Result<TData>
    {
        private Result(TData? data, string? error)
        {
            Data = data;
            Error = error;
        }

        public ResultType ResultType => Error is null ? ResultType.Success : ResultType.Failure;
        public TData? Data { get; }
        public string? Error { get; }

        public static Result<TData> Fail(string error, TData? data = default)
        {
            ArgumentNullException.ThrowIfNull(error);
            return new(data, error);
        }

        public static Result<TData> Success(TData data) => new(data, null);
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
