// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Specs;
namespace Nethermind.Evm.Test.Tracing;

public class GethLikeJavascriptTracerTests :VirtualMachineTestsBase
{
    /// <summary>
    /// Testing Javascript tracers implementation
    /// </summary>
    [Test]
    public void Js_traces_simple_filter()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(32, 0, 32)
            .STOP()
            .Done;
        string userTracer = @"
                    retVal: [],
                    step: function(log, db) { this.retVal.push(log.getPC() + ':' + log.op.toString()) },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)) },
                    result: function(ctx, db) { return this.retVal }
                ";
        GethLikeTxTrace traces = Execute(
            new GethLikeJavascriptTracer(GethTraceOptions.Default with { EnableMemory = true, Tracer = userTracer  }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();


        // test outPut of the results written into CustomTracerResult
        for (int i = 0; i < traces.CustomTracerResult.Count; i++)
        {
            dynamic arrayRet = traces.CustomTracerResult[i];
            Assert.That(arrayRet[0], Is.EqualTo("0:PUSH32"));
            Assert.That(arrayRet[1], Is.EqualTo("33:PUSH1"));
            Assert.That(arrayRet[2], Is.EqualTo("35:MSTORE"));
            Assert.That(arrayRet[3], Is.EqualTo("36:PUSH1"));
            Assert.That(arrayRet[6], Is.EqualTo("42:JUMPSUB"));
            Assert.That(arrayRet[7], Is.EqualTo("43:STOP"));
        }

    }
    [Test]
    public void Js_traces_filter_with_conditionals()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(32, 0, 32)
            .STOP()
            .Done;
        string userTracer = @"
                    retVal: [],
                    step: function(log, db) {
                        if (log.op.toNumber() == 0x60) {
                            this.retVal.push(log.getPC() + ': PUSH1');
                        } else if (log.op.toNumber() == 0x52) {
                            this.retVal.push(log.getPC() + ': MSTORE');
                        }
                    },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)); },
                    result: function(ctx, db) { return this.retVal; }
                ";
        GethLikeTxTrace traces = Execute(
            new GethLikeJavascriptTracer(GethTraceOptions.Default with { EnableMemory = true, Tracer = userTracer }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        // test outPut of the results written into CustomTracerResult
        for (int i = 0; i < traces.CustomTracerResult.Count; i++)
        {
            dynamic arrayRet = traces.CustomTracerResult[i];
            Assert.That(arrayRet[0], Is.EqualTo("33: PUSH1"));
            Assert.That(arrayRet[1], Is.EqualTo("35: MSTORE"));
            Assert.That(arrayRet[2], Is.EqualTo("36: PUSH1"));
            Assert.That(arrayRet[3], Is.EqualTo("38: PUSH1"));
            Assert.That(arrayRet[4], Is.EqualTo("40: PUSH1"));

        }

    }

    [Test]
    public void Js_traces_storage_information()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        // Store data in storage at slot 0x20
        byte[] bytecode = Prepare.EvmCode
            .SSTORE(0x20, data)
            // Copy data from storage slot 0x20 to memory
            .SLOAD(0x20)
            .MSTORE(0x40, data)
            .MCOPY(32, 0, 32)
            .STOP()
            .Done;
        string userTracer = @"
                    retVal: [],
                    step: function(log, db) {
                        if (log.op.toNumber() == 0x55)
                            this.retVal.push(log.getPC() + ': SSTORE ' + log.stack.peek(0).toString(16));
                        if (log.op.toNumber() == 0x54)
                            this.retVal.push(log.getPC() + ': SLOAD ' + log.stack.peek(0).toString(16));
                        if (log.op.toNumber() == 0x00)
                            this.retVal.push(log.getPC() + ': STOP ' + log.stack.peek(0).toString(16) + ' <- ' + log.stack.peek(1).toString(16));
                    },
                    fault: function(log, db) {
                        this.retVal.push('FAULT: ' + JSON.stringify(log));
                    },
                    result: function(ctx, db) {
                        return this.retVal;
                    }
                ";
        GethLikeTxTrace traces = Execute(
            new GethLikeJavascriptTracer(GethTraceOptions.Default with { EnableMemory = true, Tracer = userTracer }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        // test outPut of the results written into CustomTracerResult
        for (int i = 0; i < traces.CustomTracerResult.Count; i++)
        {
            dynamic arrayRet = traces.CustomTracerResult[i];
            Assert.That(arrayRet[0], Is.EqualTo("35: SSTORE 0x102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));
            Assert.That(arrayRet[1], Is.EqualTo("38: SLOAD 0x20"));
            Assert.That(arrayRet[2], Is.EqualTo("82: STOP 0x20 <- 0x0"));
        }

    }
    [Test]
    public void Js_traces_operation_results()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        // Store data in storage at slot 0x20
        byte[] bytecode = Prepare.EvmCode
            .SSTORE(0x20, data)
            // Copy data from storage slot 0x20 to memory
            .SLOAD(0x20)
            //.MSTORE(0x40, data)
            .MCOPY(32, 0, 32)
            .STOP()
            .Done;
        string userTracer = @"
                    retVal: [],
                    afterSload: false,
                    step: function(log, db) {
                        if (this.afterSload) {
                                this.retVal.push(""Result: "" + log.stack.peek(0).toString(16));
                            this.afterSload = false;
                        }
                        if (log.op.toNumber() == 0x54) {
                                this.retVal.push(log.getPC() + ""SLOAD "" + log.stack.peek(0).toString(16));
                            this.afterSload = true;
                        }
                        if (log.op.toNumber() == 0x55)
                            this.retVal.push(log.getPC() + "" SSTORE"" + log.stack.peek(0).toString(16) + "" <- "" + log.stack.peek(0).toString(16));
                    },
                    fault: function(log, db) {
                        this.retVal.push(""FAULT: "" + JSON.stringify(log));
                    },
                    result: function(ctx, db) {
                        return this.retVal;
                    }
                ";
        GethLikeTxTrace traces = Execute(
            new GethLikeJavascriptTracer(GethTraceOptions.Default with { EnableMemory = true, Tracer = userTracer }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        // test outPut of the results written into CustomTracerResult
        for (int i = 0; i < traces.CustomTracerResult.Count; i++)
        {
            dynamic arrayRet = traces.CustomTracerResult[i];
            Assert.That(arrayRet[0], Is.EqualTo("35 SSTORE0x102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f <- 0x102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));
            Assert.That(arrayRet[1], Is.EqualTo("38SLOAD 0x20"));
            Assert.That(arrayRet[2], Is.EqualTo("Result: 0x20"));
        }

    }

    /// <summary>
    /// Testing Javascript tracers implementation
    /// </summary>
    [Test]
    public void Js_traces_calls_btn_contracts()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        // Store data in storage at slot 0x20
        byte[] bytecode = Prepare.EvmCode
            .SSTORE(0x20, data)
            // Copy data from storage slot 0x20 to memory
            .SLOAD(0x20)
            //.MSTORE(0x40, data)
            .MCOPY(32, 0, 32)
            .STOP()
            .Done;

        string userTracer = @"
                    retVal: [],
                    afterSload: false,
                    callStack: [],
                    byte2Hex: function(byte) {
                        if (byte < 0x10) {
                            return ""0"" + byte.toString(16);
                        }
                        return byte.toString(16);
                    },
                    array2Hex: function(arr) {
                        var retVal = """";
                        for (var i=0; i<arr.length; i++) {
                            retVal += this.byte2Hex(arr[i]);
                        }
                        return retVal;
                    },
                    getAddr: function(log) {
                        return this.array2Hex(log.contract.getAddress());
                    },
                    step: function(log, db) {
                        var opcode = log.op.toNumber();
                        // SLOAD
                        if (opcode == 0x54) {
                            this.retVal.push(log.getPC() + "": SLOAD "" +
                                this.getAddr(log) + "":"" +
                                log.stack.peek(0).toString(16));
                            this.afterSload = true;
                        }
                        // SLOAD Result
                        if (this.afterSload) {
                            this.retVal.push(""Result: "" +
                                log.stack.peek(0).toString(16));
                            this.afterSload = false;
                        }
                        // SSTORE
                        if (opcode == 0x55) {
                            this.retVal.push(log.getPC() + "": SSTORE "" +
                                this.getAddr(log) + "":"" +
                                log.stack.peek(0).toString(16) + "" <- "" +
                                log.stack.peek(0).toString(16));
                        }
                        // End of step

                    },
                    fault: function(log, db) {
                        this.retVal.push(""FAULT: "" + JSON.stringify(log));
                    },
                    result: function(ctx, db) {
                        return this.retVal;
                }";

        GethLikeTxTrace traces = Execute(
                new GethLikeJavascriptTracer(GethTraceOptions.Default with { EnableMemory = true, Tracer = userTracer }),
                bytecode,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();

        // test outPut of the results written into CustomTracerResult
        for (int i = 0; i < traces.CustomTracerResult.Count; i++)
        {
            dynamic arrayRet = traces.CustomTracerResult[i];
            Assert.That(arrayRet[0],
                Is.EqualTo(
                    "35: SSTORE 942921b14f1b1c385cd7e0cc2ef7abe5598c8358:0x102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f <- 0x102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));
            Assert.That(arrayRet[1], Is.EqualTo("38: SLOAD 942921b14f1b1c385cd7e0cc2ef7abe5598c8358:0x20"));
            Assert.That(arrayRet[2], Is.EqualTo("Result: 0x20"));

        }
    }
    /// <summary>
    /// Testing Javascript tracers implementation
    /// </summary>
    [Test]
    public void Js_traces_Memory()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(32, 0, 32)
            .STOP()
            .Done;
        string userTracer = @"
                    retVal: [],
                    step: function(log, db) { this.retVal.push(log.getPC() + ':' + log.op.toString()) },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)) },
                    result: function(ctx, db) { return this.retVal }
                ";
        GethLikeTxTrace traces = Execute(
                new GethLikeJavascriptTracer(GethTraceOptions.Default with { EnableMemory = true, Tracer = userTracer  }),
                bytecode,
                MainnetSpecProvider.CancunActivation)
            .BuildResult();


        // test outPut of the results written into CustomTracerResult
        for (int i = 0; i < traces.CustomTracerResult.Count; i++)
        {
            dynamic arrayRet = traces.CustomTracerResult[i];
            Assert.That(arrayRet[0], Is.EqualTo("0:PUSH32"));
            Assert.That(arrayRet[1], Is.EqualTo("33:PUSH1"));
            Assert.That(arrayRet[2], Is.EqualTo("35:MSTORE"));
            Assert.That(arrayRet[3], Is.EqualTo("36:PUSH1"));
            Assert.That(arrayRet[6], Is.EqualTo("42:JUMPSUB"));
            Assert.That(arrayRet[7], Is.EqualTo("43:STOP"));
        }

    }
}
