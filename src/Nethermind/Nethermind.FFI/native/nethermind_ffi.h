// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

/*
 * Hosts a Nethermind node in-process and exposes it to C (and anything that can bind to C, e.g. Rust).
 *
 * The node runs in-process from the Nethermind.FFI build output, configured like the executable's --config plus
 * --Category.Name overrides. Only one node per process is supported: the .NET runtime cannot be unloaded or started
 * twice.
 *
 * Known limitations:
 *  - Block validation uses the L1 Ethereum validator; chains with their own (Optimism, Taiko, Xdc) are not supported.
 *  - The node may terminate the whole process (Environment.Exit on fatal database errors). Signal handling is left
 *    to the host; call nm_stop to shut down.
 *  - Linux and macOS only.
 */

#ifndef NETHERMIND_FFI_H
#define NETHERMIND_FFI_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum nm_status
{
    NM_OK = 0,
    /* The node has not finished initializing yet. */
    NM_NOT_READY = 1,
    /* The node is shutting down or has stopped. */
    NM_STOPPED = 2,
    /* The block RLP could not be decoded. */
    NM_DECODE_ERROR = 3,
    /* The block's parent is not in the block tree. */
    NM_PARENT_NOT_FOUND = 4,
    /* The parent's state is not available (pruned or not synced). */
    NM_STATE_UNAVAILABLE = 5,
    /* The block failed validation; see nm_block_result.error. */
    NM_INVALID_BLOCK = 6,
    /* An unexpected managed exception; see nm_block_result.error where available. */
    NM_INTERNAL_ERROR = 7,
} nm_status;

typedef struct nm_head
{
    uint64_t number;
    uint8_t hash[32];
    uint8_t state_root[32];
} nm_head;

/* Release with nm_free_block_result. */
typedef struct nm_block_result
{
    uint64_t gas_used;
    uint8_t state_root[32];
    uint8_t receipts_root[32];
    uint8_t block_hash[32];
    /* RLP list of the receipts in eth wire form; set when the status is NM_OK. */
    uint8_t* receipts_rlp;
    size_t receipts_len;
    /* UTF-8, not NUL-terminated; set when the status is an error that carries a message. */
    uint8_t* error;
    size_t error_len;
} nm_block_result;

typedef enum nm_tx_event
{
    /* The tx was added to the pool. */
    NM_TX_PENDING = 0,
    /* The tx left the pool, e.g. because it was included in a block. */
    NM_TX_REMOVED = 1,
    /* The tx was evicted by the pool, e.g. pushed out by its capacity limit. */
    NM_TX_EVICTED = 2,
} nm_tx_event;

/*
 * Receives tx pool events; see nm_set_tx_callback.
 *
 * event:   an nm_tx_event.
 * tx_hash: 32 bytes.
 * tx:      the EIP-2718 encoding as included in blocks (without a blob sidecar).
 * tx_hash and tx are only valid for the duration of the call.
 */
typedef void (*nm_tx_callback)(void* user_data, int32_t event, const uint8_t* tx_hash, const uint8_t* tx, size_t tx_len);

typedef struct nm_node nm_node;

/*
 * Starts the node in the background and returns immediately; use nm_wait_ready before other calls.
 *
 * ffi_dir:   directory containing Nethermind.FFI.dll (build or publish output of Nethermind.FFI). Relative paths in the
 *            config (db, logs, keystore) resolve against it.
 * config:    a config name from ffi_dir/configs (e.g. "mainnet") or a path to a config file.
 * overrides: "Category.Name=value" entries (e.g. "Init.BaseDbPath=/data/db"), taking precedence over NETHERMIND_*
 *            environment variables and the config file.
 * Returns NULL on failure and writes a NUL-terminated message to err.
 */
nm_node* nm_start(const char* ffi_dir, const char* config, int override_count, const char* const* overrides, char* err, size_t err_len);

/* Waits until the node has completed initialization: NM_OK, NM_NOT_READY on timeout, or NM_STOPPED. */
int nm_wait_ready(nm_node* node, int32_t timeout_ms);

/* Reads the current canonical head. */
int nm_get_head(nm_node* node, nm_head* head);

/*
 * Validates and executes a block on top of its parent's state without persisting anything.
 *
 * Runs the checks of a block import: header and body validation against the parent, then execution, then comparison
 * of the computed state root, receipts root, gas used and bloom with the header. The result's roots and receipts are
 * the computed ones. Always release the result with nm_free_block_result, whatever the status.
 */
int nm_execute_block(nm_node* node, const uint8_t* block_rlp, size_t block_rlp_len, nm_block_result* result);

void nm_free_block_result(nm_node* node, nm_block_result* result);

/*
 * Sets the tx pool event callback, or clears it when callback is NULL. Requires a ready node.
 *
 * The callback runs synchronously on whichever thread changed the pool (network, RPC or block processing), possibly
 * concurrently, so it must be thread-safe and fast: it delays the pool operation that raised it. A call already in
 * progress may still complete after the callback is cleared.
 *
 * Mirrors the pool's own events: a tx replaced by a higher-fee tx with the same sender and nonce is not reported, and
 * a tx dropped by the pool's eviction policy may be reported as NM_TX_REMOVED followed by NM_TX_EVICTED.
 */
int nm_set_tx_callback(nm_node* node, nm_tx_callback callback, void* user_data);

/* Requests the node to shut down with the given process exit code; returns without waiting. */
int nm_stop(nm_node* node, int32_t exit_code);

/* Waits for the node to stop, releases the handle and returns the node's exit code. */
int nm_join(nm_node* node);

#ifdef __cplusplus
}
#endif

#endif
