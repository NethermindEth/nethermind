

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.DataModel;
using Nethermind.LibSolc;
using Nethermind.Network;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Module
{
    public class NethmModule : ModuleBase, INethmModule
    {
        private readonly IEnode _enode;

        public NethmModule(IConfigProvider configurationProvider, ILogManager logManager,
            IJsonSerializer jsonSerializer, IEnode enode) : base(configurationProvider, logManager, jsonSerializer)
        {
            _enode = enode;
        }
            
        public ResultWrapper<IEnumerable<string>> nethm_getCompilers()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Compiles Solidity code
        /// </summary>
        /// <param name="parameters"> A JSON string containing the contract to compile and extra parameters to feed
        /// to the compiler
        /// </param>
        /// <returns>Returns the raw compiler output containing the bytecode and metadata; or errors encountered by the
        /// compiler whilst trying to compile
        /// </returns>
        /// <example>
        /// This example shows how a sample 'params' input should look like when running a JSON RPC call
        /// <code>
        /// params: [{
        /// "Contract": "pragma solidity ^0.4.22; contract test { function multiply(uint a) public returns(uint d) {return a * 7;} }",
        /// "EvmVersion": "byzantium", //optional
        /// "Optimize": true,
        /// "Runs": 200  //optional
        /// }]
        /// </code>
        /// </example>
        
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
        
        public ResultWrapper<byte[]> nethm_compileLLL(string code)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<byte[]> nethm_compileSerpent(string code)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<string> enode_info()
            => ResultWrapper<string>.Success(_enode.Info);

        public override ModuleType ModuleType => ModuleType.Nethm;
    }
}