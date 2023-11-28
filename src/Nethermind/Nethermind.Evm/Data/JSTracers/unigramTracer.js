// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Tracer originally developed by go-ethereum: https://github.com/ethereum/go-ethereum/blob/58297e339b26d09a0c21e550ee4b6ed6205cedcd/eth/tracers/js/internal/tracers/unigram_tracer.js

{
    // hist is the map of opcodes to counters
    hist: {},
    // nops counts number of ops
    nops: 0,
    // step is invoked for every opcode that the VM executes.
    step: function(log, db) {
        var op = log.op.toString();
        if (this.hist[op]){
            this.hist[op]++;
        }
        else {
            this.hist[op] = 1;
        }
        this.nops++;
    },
    // fault is invoked when the actual execution of an opcode fails.
    fault: function(log, db) {},

    // result is invoked when all the opcodes have been iterated over and returns
    // the final result of the tracing.
    result: function(ctx) {
        return this.hist;
    },
}
