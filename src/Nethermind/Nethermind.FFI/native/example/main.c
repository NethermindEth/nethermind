// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Usage: example <ffi_dir> <config> [Category.Name=value...]
// Starts the node, prints the head, then executes each hex-encoded block RLP read from stdin, one per line.
// Stops the node on end of input.

#include "../nethermind_ffi.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void print_hex(const char* label, const uint8_t* bytes, size_t length)
{
    printf("%s0x", label);
    for (size_t i = 0; i < length; i++) printf("%02x", bytes[i]);
    printf("\n");
}

static size_t parse_hex(char* line, uint8_t* out)
{
    char* hex = strncmp(line, "0x", 2) == 0 ? line + 2 : line;
    size_t length = strcspn(hex, "\r\n") / 2;
    for (size_t i = 0; i < length; i++) sscanf(hex + 2 * i, "%2hhx", &out[i]);
    return length;
}

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "Usage: %s <ffi_dir> <config> [Category.Name=value...]\n", argv[0]);
        return 1;
    }

    char err[1024];
    nm_node* node = nm_start(argv[1], argv[2], argc - 3, (const char* const*)argv + 3, err, sizeof err);
    if (node == NULL)
    {
        fprintf(stderr, "nm_start: %s\n", err);
        return 1;
    }

    int status = nm_wait_ready(node, 600000);
    printf("ready: %d\n", status);
    if (status == NM_OK)
    {
        nm_head head;
        status = nm_get_head(node, &head);
        printf("head: %d\n", status);
        if (status == NM_OK)
        {
            printf("head number: %llu\n", (unsigned long long)head.number);
            print_hex("head hash: ", head.hash, sizeof head.hash);
            print_hex("head state root: ", head.state_root, sizeof head.state_root);
        }

        size_t capacity = 0;
        char* line = NULL;
        while (getline(&line, &capacity, stdin) > 0)
        {
            uint8_t* block = malloc(strlen(line) / 2 + 1);
            size_t block_length = parse_hex(line, block);

            nm_block_result result;
            status = nm_execute_block(node, block, block_length, &result);
            printf("execute: %d gas used %llu\n", status, (unsigned long long)result.gas_used);
            print_hex("  block hash: ", result.block_hash, sizeof result.block_hash);
            print_hex("  state root: ", result.state_root, sizeof result.state_root);
            print_hex("  receipts root: ", result.receipts_root, sizeof result.receipts_root);
            printf("  receipts rlp: %zu bytes\n", result.receipts_len);
            if (result.error != NULL) printf("  error: %.*s\n", (int)result.error_len, (const char*)result.error);
            fflush(stdout);

            nm_free_block_result(node, &result);
            free(block);
        }

        free(line);
    }

    nm_stop(node, 0);
    int exit_code = nm_join(node);
    printf("exit code: %d\n", exit_code);
    return exit_code;
}
