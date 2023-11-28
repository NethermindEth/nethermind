// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Tracer originally developed by go-ethereum: https://github.com/ethereum/go-ethereum/blob/58297e339b26d09a0c21e550ee4b6ed6205cedcd/eth/tracers/js/internal/tracers/noop_tracer_legacy.js

// noopTracer is just the barebone boilerplate code required from a JavaScript
// object to be usable as a transaction tracer.
{
	// step is invoked for every opcode that the VM executes.
	step: function(log, db) { },

	// fault is invoked when the actual execution of an opcode fails.
	fault: function(log, db) { },

	// result is invoked when all the opcodes have been iterated over and returns
	// the final result of the tracing.
	result: function(ctx, db) { return {}; }
}
