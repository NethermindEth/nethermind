// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;

namespace Nethermind.JsonRpc;

public class JsonRpcService : IJsonRpcService
{
    private readonly ILogger _logger;
    private readonly IRpcModuleProvider _rpcModuleProvider;
    private readonly HashSet<string> _methodsLoggingFiltering;
    private readonly int _maxLoggedRequestParametersCharacters;

    public JsonRpcService(IRpcModuleProvider rpcModuleProvider, ILogManager logManager, IJsonRpcConfig jsonRpcConfig)
    {
        _logger = logManager.GetClassLogger();
        _rpcModuleProvider = rpcModuleProvider;
        _methodsLoggingFiltering = (jsonRpcConfig.MethodsLoggingFiltering ?? Array.Empty<string>()).ToHashSet();
        _maxLoggedRequestParametersCharacters = jsonRpcConfig.MaxLoggedRequestParametersCharacters ?? int.MaxValue;
    }

    public async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
    {
        try
        {
            (int? errorCode, string errorMessage) = Validate(rpcRequest, context);
            if (errorCode.HasValue)
            {
                return GetErrorResponse(rpcRequest.Method, errorCode.Value, errorMessage, null, rpcRequest.Id);
            }

            try
            {
                return await ExecuteRequestAsync(rpcRequest, context);
            }
            catch (TargetInvocationException ex)
            {
                if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex.InnerException);
                return GetErrorResponse(rpcRequest.Method, ErrorCodes.InternalError, "Internal error", ex.InnerException?.ToString(), rpcRequest.Id);
            }
            catch (ModuleRentalTimeoutException ex)
            {
                if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
                return GetErrorResponse(rpcRequest.Method, ErrorCodes.ModuleTimeout, "Timeout", ex.ToString(), rpcRequest.Id);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
                return GetErrorResponse(rpcRequest.Method, ErrorCodes.InternalError, "Internal error", ex.ToString(), rpcRequest.Id);
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"Error during validation, request: {rpcRequest}", ex);
            return GetErrorResponse(ErrorCodes.ParseError, "Parse error");
        }
    }

    private async Task<JsonRpcResponse> ExecuteRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
    {
        string methodName = rpcRequest.Method.Trim();

        (MethodInfo MethodInfo, bool ReadOnly) result = _rpcModuleProvider.Resolve(methodName);
        return result.MethodInfo is not null
            ? await ExecuteAsync(rpcRequest, methodName, result, context)
            : GetErrorResponse(methodName, ErrorCodes.MethodNotFound, "Method not found", $"{rpcRequest.Method}", rpcRequest.Id);
    }

    private async Task<JsonRpcResponse> ExecuteAsync(JsonRpcRequest request, string methodName,
        (MethodInfo Info, bool ReadOnly) method, JsonRpcContext context)
    {
        ParameterInfo[] expectedParameters = method.Info.GetParameters();
        JsonElement providedParameters = request.Params;

        LogRequest(methodName, providedParameters, expectedParameters);

        int missingParamsCount = expectedParameters.Length - providedParameters.GetArrayLength();
        foreach (JsonElement item in providedParameters.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null || (item.ValueKind == JsonValueKind.String && item.ValueEquals(ReadOnlySpan<byte>.Empty)))
            {
                missingParamsCount++;
            }
        }

        int explicitNullableParamsCount = 0;

        if (missingParamsCount != 0)
        {
            bool hasIncorrectParameters = true;
            if (missingParamsCount > 0)
            {
                hasIncorrectParameters = false;
                for (int i = 0; i < missingParamsCount; i++)
                {
                    int parameterIndex = expectedParameters.Length - missingParamsCount + i;
                    bool nullable = IsNullableParameter(expectedParameters[parameterIndex]);

                    // if the null is the default parameter it could be passed in an explicit way as "" or null
                    // or we can treat null as a missing parameter. Two tests for this cases:
                    // Eth_call_is_working_with_implicit_null_as_the_last_argument and Eth_call_is_working_with_explicit_null_as_the_last_argument
                    bool isExplicit = providedParameters.GetArrayLength() >= parameterIndex + 1;
                    if (nullable && isExplicit)
                    {
                        explicitNullableParamsCount += 1;
                    }
                    if (!expectedParameters[expectedParameters.Length - missingParamsCount + i].IsOptional && !nullable)
                    {
                        hasIncorrectParameters = true;
                        break;
                    }
                }
            }

            if (hasIncorrectParameters)
            {
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", $"Incorrect parameters count, expected: {expectedParameters.Length}, actual: {expectedParameters.Length - missingParamsCount}", request.Id);
            }
        }

        missingParamsCount -= explicitNullableParamsCount;

        //prepare parameters
        object[]? parameters = null;
        if (expectedParameters.Length > 0)
        {
            parameters = DeserializeParameters(expectedParameters, providedParameters, missingParamsCount);
            if (parameters is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Incorrect JSON RPC parameters when calling {methodName} with params [{string.Join(", ", providedParameters)}]");
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, request.Id);
            }
        }

        //execute method
        IResultWrapper resultWrapper = null;
        IRpcModule rpcModule = await _rpcModuleProvider.Rent(methodName, method.ReadOnly);
        if (rpcModule is IContextAwareRpcModule contextAwareModule)
        {
            contextAwareModule.Context = context;
        }
        bool returnImmediately = methodName != "eth_getLogs";
        Action? returnAction = returnImmediately ? (Action)null : () => _rpcModuleProvider.Return(methodName, rpcModule);
        try
        {
            object invocationResult = method.Info.Invoke(rpcModule, parameters);
            switch (invocationResult)
            {
                case IResultWrapper wrapper:
                    resultWrapper = wrapper;
                    break;
                case Task task:
                    await task;
                    resultWrapper = task.GetType().GetProperty("Result")?.GetValue(task) as IResultWrapper;
                    break;
            }
        }
        catch (TargetParameterCountException e)
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, e.Message, e.ToString(), request.Id, returnAction);
        }
        catch (TargetInvocationException e) when (e.InnerException is JsonException)
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", e.InnerException?.ToString(), request.Id, returnAction);
        }
        catch (Exception e) when (e.InnerException is OperationCanceledException)
        {
            string errorMessage = $"{methodName} request was canceled due to enabled timeout.";
            return GetErrorResponse(methodName, ErrorCodes.Timeout, errorMessage, null, request.Id, returnAction);
        }
        catch (Exception e) when (e.InnerException is InsufficientBalanceException)
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidInput, e.InnerException.Message, e.ToString(), request.Id, returnAction);
        }
        finally
        {
            if (returnImmediately)
            {
                _rpcModuleProvider.Return(methodName, rpcModule);
            }
        }

        if (resultWrapper is null)
        {
            string errorMessage = $"Method {methodName} execution result does not implement IResultWrapper";
            if (_logger.IsError) _logger.Error(errorMessage);
            return GetErrorResponse(methodName, ErrorCodes.InternalError, errorMessage, null, request.Id, returnAction);
        }

        Result? result = resultWrapper.GetResult();
        if (result is null)
        {
            if (_logger.IsError) _logger.Error($"Error during method: {methodName} execution: no result");
            return GetErrorResponse(methodName, resultWrapper.GetErrorCode(), "Internal error", resultWrapper.GetData(), request.Id, returnAction);
        }

        if (result.ResultType == ResultType.Failure)
        {
            return GetErrorResponse(methodName, resultWrapper.GetErrorCode(), result.Error, resultWrapper.GetData(), request.Id, returnAction);
        }

        return GetSuccessResponse(methodName, resultWrapper.GetData(), request.Id, returnAction);
    }

    private void LogRequest(string methodName, JsonElement providedParameters, ParameterInfo[] expectedParameters)
    {
        if (_logger.IsInfo && !_methodsLoggingFiltering.Contains(methodName))
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("Executing JSON RPC call ");
            builder.Append(methodName);
            builder.Append(" with params [");

            int paramsLength = 0;
            int paramsCount = 0;
            const string separator = ", ";

            if (providedParameters.ValueKind != JsonValueKind.Undefined && providedParameters.ValueKind != JsonValueKind.Null)
            {
                foreach (JsonElement param in providedParameters.EnumerateArray())
                {
                    string? parameter = expectedParameters.ElementAtOrDefault(paramsCount)?.Name == "passphrase"
                        ? "{passphrase}"
                        : param.GetRawText();

                    if (paramsLength > _maxLoggedRequestParametersCharacters)
                    {
                        int toRemove = paramsLength - _maxLoggedRequestParametersCharacters;
                        builder.Remove(builder.Length - toRemove, toRemove);
                        builder.Append("...");
                        break;
                    }

                    if (paramsCount != 0)
                    {
                        builder.Append(separator);
                        paramsLength += separator.Length;
                    }

                    builder.Append(parameter);
                    paramsLength += (parameter?.Length ?? 0);
                    paramsCount++;
                }
            }
            builder.Append(']');
            string log = builder.ToString();
            _logger.Info(log);
        }
    }

    private object[]? DeserializeParameters(ParameterInfo[] expectedParameters, JsonElement providedParameters, int missingParamsCount)
    {
        try
        {
            List<object> executionParameters = new List<object>();
            int i = 0;
            foreach (JsonElement providedParameter in providedParameters.EnumerateArray())
            {
                ParameterInfo expectedParameter = expectedParameters[i];
                i++;

                Type paramType = expectedParameter.ParameterType;
                if (paramType.IsByRef)
                {
                    paramType = paramType.GetElementType();
                }

                if (providedParameter.ValueKind == JsonValueKind.Null || (providedParameter.ValueKind == JsonValueKind.String && providedParameter.ValueEquals(ReadOnlySpan<byte>.Empty)))
                {
                    if (providedParameter.ValueKind == JsonValueKind.Null && IsNullableParameter(expectedParameter))
                    {
                        executionParameters.Add(null);
                    }
                    else
                    {
                        executionParameters.Add(Type.Missing);
                    }
                    continue;
                }

                object? executionParam;
                if (paramType.IsAssignableTo(typeof(IJsonRpcParam)))
                {
                    IJsonRpcParam jsonRpcParam = (IJsonRpcParam)Activator.CreateInstance(paramType);
                    jsonRpcParam!.ReadJson(providedParameter, EthereumJsonSerializer.JsonOptions);
                    executionParam = jsonRpcParam;
                }
                else if (paramType == typeof(string))
                {
                    executionParam = providedParameter.GetString();
                }
                else
                {
                    if (providedParameter.ValueKind == JsonValueKind.String)
                    {
                        JsonConverter converter = EthereumJsonSerializer.JsonOptions.GetConverter(paramType);
                        if (converter.GetType().FullName.StartsWith("System."))
                        {
                            executionParam = JsonSerializer.Deserialize(providedParameter.GetString(), paramType, EthereumJsonSerializer.JsonOptions);
                        }
                        else
                        {
                            executionParam = providedParameter.Deserialize(paramType, EthereumJsonSerializer.JsonOptions);
                        }
                    }
                    else
                    {
                        executionParam = providedParameter.Deserialize(paramType, EthereumJsonSerializer.JsonOptions);
                    }
                }

                executionParameters.Add(executionParam);
            }

            for (i = 0; i < missingParamsCount; i++)
            {
                executionParameters.Add(Type.Missing);
            }

            return executionParameters.ToArray();
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn("Error while parsing JSON RPC request parameters " + e);
            return null;
        }
    }

    private bool IsNullableParameter(ParameterInfo parameterInfo)
    {
        Type parameterType = parameterInfo.ParameterType;
        if (parameterType.IsValueType)
        {
            return Nullable.GetUnderlyingType(parameterType) is not null;
        }

        CustomAttributeData nullableAttribute = parameterInfo.CustomAttributes
            .FirstOrDefault(x => x.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (nullableAttribute is not null)
        {
            CustomAttributeTypedArgument attributeArgument = nullableAttribute.ConstructorArguments.FirstOrDefault();
            if (attributeArgument.ArgumentType == typeof(byte))
            {
                return (byte)attributeArgument.Value! == 2;
            }
        }
        return false;
    }

    private JsonRpcResponse GetSuccessResponse(string methodName, object result, object id, Action? disposableAction)
    {
        JsonRpcResponse response = new JsonRpcSuccessResponse(disposableAction)
        {
            Result = result,
            Id = id,
            MethodName = methodName
        };

        return response;
    }

    public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage) =>
        GetErrorResponse(null, errorCode, errorMessage, null, null);

    public JsonRpcErrorResponse GetErrorResponse(string methodName, int errorCode, string errorMessage, object id) =>
        GetErrorResponse(methodName, errorCode, errorMessage, null, id);

    private JsonRpcErrorResponse GetErrorResponse(string? methodName, int errorCode, string? errorMessage, object? errorData, object? id, Action? disposableAction = null)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending error response, method: {methodName ?? "none"}, id: {id}, errorType: {errorCode}, message: {errorMessage}, errorData: {errorData}");
        JsonRpcErrorResponse response = new(disposableAction)
        {
            Error = new Error
            {
                Code = errorCode,
                Message = errorMessage,
                Data = errorData,
            },
            Id = id,
            MethodName = methodName
        };

        return response;
    }

    private (int? ErrorType, string ErrorMessage) Validate(JsonRpcRequest? rpcRequest, JsonRpcContext context)
    {
        if (rpcRequest is null)
        {
            return (ErrorCodes.InvalidRequest, "Invalid request");
        }

        string methodName = rpcRequest.Method;
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return (ErrorCodes.InvalidRequest, "Method is required");
        }

        methodName = methodName.Trim();

        ModuleResolution result = _rpcModuleProvider.Check(methodName, context);
        return result switch
        {
            ModuleResolution.Unknown => ((int?)ErrorCodes.MethodNotFound, $"Method {methodName} is not supported"),
            ModuleResolution.Disabled => (ErrorCodes.InvalidRequest, $"{methodName} found but the containing module is disabled for the url '{context.Url?.ToString() ?? string.Empty}', consider adding module in JsonRpcConfig.AdditionalRpcUrls for additional url, or to JsonRpcConfig.EnabledModules for default url"),
            ModuleResolution.EndpointDisabled => (ErrorCodes.InvalidRequest, $"{methodName} found for the url '{context.Url?.ToString() ?? string.Empty}' but is disabled for {context.RpcEndpoint}"),
            ModuleResolution.NotAuthenticated => (ErrorCodes.InvalidRequest, $"Method {methodName} should be authenticated"),
            _ => (null, null)
        };
    }
}
