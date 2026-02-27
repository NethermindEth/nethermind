#!/usr/bin/env bash
# Usage:
#   ./stateless_exec_test.sh
#
# Args:
#   (none)
#
# Env (required when running on non-riscv64; used to SSH to the RISC-V host):
#   STATELESS_EXECUTOR_RISCV_HOST            Remote host/IP
#   STATELESS_EXECUTOR_RISCV_USERNAME        SSH username
#   STATELESS_EXECUTOR_RISCV_SSH_PRIVATE_KEY SSH private key content (OpenSSH format)
#
# Notes:
#   - Copies ./Program and this script to /tmp/StatelessExecutor on the remote host, then runs it there.
export TOP_DIR="$(cd "$(dirname "$0")" ; pwd -P)"

function fail() {
    local err_code="$1"

    shift 1

    echo "Execution failed: $@" >&2
    exit $err_code
}

function require_env() {
    local name="$1"
    local value="${!name}"

    if [ -z "$value" ] ; then
        fail 10 "Missing required env var: $name"
    fi
}

if [ "$(uname -m)" != "riscv64" ] ; then
    # On non-riscv64: upload artifacts and run on the RISC-V host via SSH.
    require_env STATELESS_EXECUTOR_RISCV_USERNAME
    require_env STATELESS_EXECUTOR_RISCV_HOST
    require_env STATELESS_EXECUTOR_RISCV_SSH_PRIVATE_KEY

    ssh_user="$STATELESS_EXECUTOR_RISCV_USERNAME"
    ssh_host="$STATELESS_EXECUTOR_RISCV_HOST"

    # SSH expects a key file, so we materialize the key content into a temp file and delete it on exit.
    key_tmp_path=""
    cleanup_key_tmp() {
        if [ -n "$key_tmp_path" ] && [ -f "$key_tmp_path" ] ; then
            rm -f "$key_tmp_path"
        fi
    }
    trap cleanup_key_tmp EXIT INT TERM

    key_tmp_path="$(mktemp -t stateless_executor_ssh_key.XXXXXX)" \
        || fail 11 "Failed to create temp key file"
    chmod 600 "$key_tmp_path" \
        || fail 11 "Failed to chmod temp key file: $key_tmp_path"

    ssh_key_content="$(printf "%s" "$STATELESS_EXECUTOR_RISCV_SSH_PRIVATE_KEY")"
    if [ -z "$ssh_key_content" ] ; then
        fail 11 "SSH private key content is empty"
    fi
    printf "%s\n" "$ssh_key_content" > "$key_tmp_path" \
        || fail 11 "Failed to write temp key file: $key_tmp_path"

    # Remote layout is fixed: everything lives under /tmp/StatelessExecutor.
    remote_dir="/tmp/StatelessExecutor"
    remote_bin="$remote_dir/StatelessExecutor"
    remote_script="$remote_dir/stateless_exec_test.sh"

    # Local artifacts produced by the build in this directory.
    local_bin="${TOP_DIR}/Program"
    local_script="${TOP_DIR}/stateless_exec_test.sh"

    if [ ! -f "$local_bin" ] ; then
        fail 12 "Local binary not found (expected): $local_bin"
    fi
    if [ ! -f "$local_script" ] ; then
        fail 12 "Local script not found (expected): $local_script"
    fi

    ssh_opts=(-i "$key_tmp_path" -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new)

    # Create remote dir, copy binary + script, chmod, then execute remotely.
    ssh "${ssh_opts[@]}" "$ssh_user@$ssh_host" "mkdir -p \"$remote_dir\" && chmod 700 \"$remote_dir\"" \
        || fail 13 "Failed to create remote dir: $remote_dir"
    scp "${ssh_opts[@]}" "$local_bin" "$ssh_user@$ssh_host:$remote_bin" \
        || fail 14 "Failed to upload binary to: $ssh_host:$remote_bin"
    scp "${ssh_opts[@]}" "$local_script" "$ssh_user@$ssh_host:$remote_script" \
        || fail 14 "Failed to upload script to: $ssh_host:$remote_script"

    ssh "${ssh_opts[@]}" "$ssh_user@$ssh_host" "chmod 700 \"$remote_bin\" && chmod 700 \"$remote_script\" && \"$remote_script\""
    exit $?
fi

cat > /tmp/riscv.gdb <<'GDB'
set architecture riscv:rv64
set pagination off
set confirm off
set print pretty on
set print asm-demangle on

# Make sure we capture full output even if gdb would normally page/truncate
set height 0
set width 0

define dump_sigsegv_state
  echo \n===== SIGSEGV =====\n
  bt
  echo \n--- registers ---\n
  info registers
  echo \n--- symbols (pc/ra) ---\n
  echo pc:\n
  info symbol $pc
  echo ra:\n
  info symbol $ra
  echo \n===== END SIGSEGV =====\n
end

# Print a stacktrace on hard crashes.
catch signal SIGSEGV
commands
  dump_sigsegv_state
  quit
end

# Print a stacktrace on runtime-thrown exceptions.
break RhpThrowEx
commands
  echo \n===== RhpThrowEx =====\n
  bt
  echo \n===== END RhpThrowEx =====\n
  continue
end

run
GDB

echo Executing StatelessExecutor...
/tmp/StatelessExecutor/StatelessExecutor
err_code="$?"
if [ "$err_code" == "0" ] ; then
    echo "Execution succeeded" >&2
    exit 0
fi
#./tmp-dbg.sh /tmp/StatelessExecutor/StatelessExecutor &
gdb -q -x /tmp/riscv.gdb --args /tmp/StatelessExecutor/StatelessExecutor
exit 1
