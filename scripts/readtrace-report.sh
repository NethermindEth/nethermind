#!/usr/bin/env bash
# Summarize ReadTrace per-read provenance CSVs (produced by --Blocks.ReadTraceOutput)
# without loading them into memory/context. Works on the .csv.gz events file and the
# readtrace-blocks-*.csv per-block summary from the expb readtrace artifact.
#
# Events schema:  block,tx,seq,kind,layer,prov,address,slot,rocks_us
#   kind:  A (account) | S (slot)
#   layer: IntraTx|BlockDict|PreBlock|WriteBuf|BundleSnap|SnapWindow|CarryFwd|RocksDb|Unknown
#   prov:  pw (prewarmer) | mb (main backfill) | bal — only for PreBlock hits
#
# Usage:
#   readtrace-report.sh summary    <readtrace-blocks-*.csv>            # per-layer totals + shares
#   readtrace-report.sh block      <readtrace-*.csv.gz> <block>        # per-tx breakdown of one block
#   readtrace-report.sh tx         <readtrace-*.csv.gz> <block> <tx> [limit]  # ordered asks of one tx
#   readtrace-report.sh rocksdb    <readtrace-*.csv.gz> [N]            # top keys by RocksDB reads/latency
#   readtrace-report.sh recurrence <readtrace-*.csv.gz> [window]       # do RocksDB-served keys recur across blocks?
#   readtrace-report.sh export     <readtrace-*.csv.gz> <block> [out]  # extract one block as plain CSV

set -euo pipefail

cmd="${1:-}"
file="${2:-}"
[[ -z "${cmd}" || -z "${file}" ]] && { grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 1; }
[[ -f "${file}" ]] || { echo "No such file: ${file}" >&2; exit 1; }

# `|| true` masks SIGPIPE (rc 141) when a downstream consumer (head/awk exit) closes early —
# otherwise `set -o pipefail` fails commands that produced correct output.
cat_file() { zcat -f "${file}" 2>/dev/null || true; }

case "${cmd}" in
  summary)
    # Input: readtrace-blocks CSV (block,txs,kind,events,dropped,intratx,blockdict,preblock,
    #        preblock_pw,preblock_mb,preblock_bal,writebuf,bundlesnap,snapwindow,carryfwd,rocksdb,unknown,rocks_ms)
    cat_file | awk -F',' 'NR>1 {
        k=$3; blocks[$1]=1
        events[k]+=$4; dropped[k]+=$5; intratx[k]+=$6; blockdict[k]+=$7; preblock[k]+=$8
        pw[k]+=$9; mb[k]+=$10; bal[k]+=$11; writebuf[k]+=$12; bundlesnap[k]+=$13
        snapwin[k]+=$14; carryfwd[k]+=$15; rocksdb[k]+=$16; unknown[k]+=$17; rocksms[k]+=$18
      }
      END {
        nb=0; for (b in blocks) nb++
        printf "blocks: %d   dropped events: %d\n\n", nb, dropped["A"] + dropped["S"]
        for (i=1; i<=2; i++) {
          k = (i==1) ? "A" : "S"
          label = (i==1) ? "ACCOUNTS" : "SLOTS"
          t = events[k]; if (t == 0) continue
          deep = preblock[k]+writebuf[k]+bundlesnap[k]+snapwin[k]+carryfwd[k]+rocksdb[k]+unknown[k]
          printf "%s  total asks: %d (%.0f/block)\n", label, t, t/nb
          printf "  already in scope     : IntraTx %d (%.1f%%)  BlockDict %d (%.1f%%)\n", intratx[k], 100*intratx[k]/t, blockdict[k], 100*blockdict[k]/t
          printf "  first-touch resolves : %d (%.0f/block)\n", deep, deep/nb
          printf "    PreBlockCache      : %d (%.1f%% of first-touch)  [prewarmer %d | main-backfill %d | bal %d]\n", preblock[k], deep?100*preblock[k]/deep:0, pw[k], mb[k], bal[k]
          printf "    bundle write-buf   : %d (%.1f%%)\n", writebuf[k], deep?100*writebuf[k]/deep:0
          printf "    snapshot window    : %d (%.1f%%)   <- recent blocks still in memory\n", snapwin[k]+bundlesnap[k], deep?100*(snapwin[k]+bundlesnap[k])/deep:0
          printf "    CarryForward       : %d (%.1f%%)   <- FlatDb cross-block cache\n", carryfwd[k], deep?100*carryfwd[k]/deep:0
          printf "    RocksDB            : %d (%.1f%%)   %.2f ms total (%.2f ms/block)\n", rocksdb[k], deep?100*rocksdb[k]/deep:0, rocksms[k], rocksms[k]/nb
          if (unknown[k] > 0) printf "    Unknown            : %d (%.1f%%)\n", unknown[k], 100*unknown[k]/deep
          printf "\n"
        }
      }'
    ;;

  block)
    b="${3:?block number required}"
    cat_file | awk -F',' -v b="${b}" 'NR>1 && $1==b {
        tx=$2; n[tx]++
        if ($5=="RocksDb") { r[tx]++; rus[tx]+=$9 }
        else if ($5=="PreBlock") { p[tx]++; if ($6=="pw") ppw[tx]++ }
        else if ($5=="CarryFwd") c[tx]++
        else if ($5=="SnapWindow" || $5=="BundleSnap") s[tx]++
        else if ($5=="WriteBuf") w[tx]++
        else if ($5=="IntraTx" || $5=="BlockDict") loc[tx]++
        else u[tx]++
        if (!seen) { min=tx; max=tx; seen=1 }
        if (tx>max) max=tx; if (tx<min) min=tx
      }
      END {
        if (!seen) { print "No rows for block " b; exit }
        printf "%-6s %8s %8s %10s %8s %8s %8s %8s %8s %12s\n", "tx", "asks", "local", "preblock", "(pw)", "writebuf", "snapwin", "carryfwd", "unknown", "rocksdb(ms)"
        for (t=min; t<=max; t++) if (n[t]>0)
          printf "%-6d %8d %8d %10d %8d %8d %8d %8d %8d %6d (%.2f)\n", t, n[t], loc[t], p[t], ppw[t], w[t], s[t], c[t], u[t], r[t], rus[t]/1000
      }'
    ;;

  tx)
    b="${3:?block number required}"; t="${4:?tx index required}"; lim="${5:-200}"
    cat_file | awk -F',' -v b="${b}" -v t="${t}" -v lim="${lim}" '
      NR==1 { print; next }
      $1==b && $2==t { print; if (++i>=lim) exit }'
    ;;

  rocksdb)
    n="${3:-30}"
    tmp="$(mktemp)"
    cat_file | awk -F',' 'NR>1 && $5=="RocksDb" {
        key=$7; if ($4=="S") key=key":"$8
        cnt[key]++; us[key]+=$9
      }
      END { for (k in cnt) printf "%10d %12.1f  %s\n", cnt[k], us[k]/1000, k }' \
      | sort -rn > "${tmp}"
    echo "     reads     total_ms  key"
    head -n "${n}" "${tmp}"
    rm -f "${tmp}"
    ;;

  recurrence)
    w="${3:-32}"
    cat_file | awk -F',' -v w="${w}" 'NR>1 && $5=="RocksDb" {
        key=$4":"$7":"$8; b=$1+0
        total++
        if (key in last) { if (b-last[key]<=w && b>last[key]) recur++; if (b>last[key]) reseen++ }
        last[key]=b
      }
      END {
        nk=0; for (k in last) nk++
        printf "RocksDB-served asks: %d   distinct keys: %d\n", total, nk
        printf "asks whose key was RocksDB-served in an EARLIER block: %d (%.1f%%)\n", reseen, total?100*reseen/total:0
        printf "  ... within the last %d blocks: %d (%.1f%%)   <- cross-block warming ceiling\n", w, recur, total?100*recur/total:0
      }'
    ;;

  export)
    b="${3:?block number required}"; out="${4:-readtrace-block-${b}.csv}"
    cat_file | awk -F',' -v b="${b}" 'NR==1 || $1==b' > "${out}"
    echo "Wrote $(wc -l < "${out}") lines to ${out}"
    ;;

  *)
    echo "Unknown command: ${cmd}" >&2; exit 1
    ;;
esac
