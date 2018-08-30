

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Nethermind.Config;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.DataModel;
using Nethermind.LibSolc;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Module
{
    public class NethmModule : ModuleBase, INethmModule
    {
        public NethmModule(IConfigProvider configurationProvider, ILogManager logManager) : base(configurationProvider,
            logManager)
        {
            
        }
            
        public ResultWrapper<IEnumerable<string>> nethm_getCompilers()
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<string> nethm_compileSolidity(string parameters)
        {            
            CompilerParameters compilerParameters = new CompilerParameters();
            compilerParameters.FromJson(parameters);
            
            Match match = Regex.Match(compilerParameters.Contract,@"contract (.*?) ");
            string contractName = match.Groups[1].Value;

            string result = Proxy.Compile(compilerParameters.Contract, compilerParameters.EvmVersion,
                compilerParameters.Optimize, compilerParameters.Runs);

            JObject parsedResult = JObject.Parse(result);
            string byteCode = (string) parsedResult["contracts"][contractName][contractName]["evm"]["bytecode"]["object"];

            return byteCode == null
                ? ResultWrapper<string>.Fail((string) parsedResult["errors"])
                : ResultWrapper<string>.Success(result); //returning the entire compiler output instead of just the bytecode
        }
        
        public ResultWrapper<Data> nethm_compileLLL(string code)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> nethm_compileSerpent(string code)
        {
            throw new NotImplementedException();
        }
    }
}