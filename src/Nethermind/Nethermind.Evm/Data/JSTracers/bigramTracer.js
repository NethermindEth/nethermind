// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Tracer originally developed by go-ethereum: https://github.com/ethereum/go-ethereum/blob/58297e339b26d09a0c21e550ee4b6ed6205cedcd/eth/tracers/js/internal/tracers/bigram_tracer.js

{
    // hist is the counters of opcode bigrams
    hist: {},
    // lastOp is last operation
    lastOp: '',
    // execution depth of last op
    lastDepth: 0,
    // step is invoked for every opcode that the VM executes.
    step: function(log, db) {
        var op = log.op.toString();
        var depth = log.getDepth();
        if (depth == this.lastDepth){
            var key = this.lastOp+'-'+op;
            if (this.hist[key]){
                this.hist[key]++;
            }
            else {
                this.hist[key] = 1;
            }
        }
        this.lastOp = op;
        this.lastDepth = depth;
    },
    // fault is invoked when the actual execution of an opcode fails.
    fault: function(log, db) {},
    // result is invoked when all the opcodes have been iterated over and returns
    // the final result of the tracing.
    result: function(ctx) {
        return this.hist;
    },
}
