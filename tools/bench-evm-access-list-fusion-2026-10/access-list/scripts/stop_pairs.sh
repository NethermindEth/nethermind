#!/bin/bash
# Stops my own pairab runner (and its chain harness children) by PID.
for p in $(ps -eo pid,args | awk '/[p]airab.sh/ {print $1}'); do kill $p 2>/dev/null; done
for p in $(ps -eo pid,args | awk '/cp-(base|br|ref)\/out\/[c]hainprobe/ {print $1}'); do kill $p 2>/dev/null; done
sleep 1; ps -eo pid,args | grep -E "[p]airab|[c]hainprobe" | cut -c1-80
