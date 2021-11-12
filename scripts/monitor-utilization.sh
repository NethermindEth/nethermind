#!/bin/bash     

cpu_utilization () {
  CPU_UTILIZATION=$(top -bn1 | grep "Cpu(s)" | sed "s/.*, *\([0-9.]*\)%* id.*/\1/" | awk '{print 100 - $1"%"}')
  echo "CPU utilization: $CPU_UTILIZATION"
}

memory_utilization (){
  MEM_UTILIZATION=$(free -m | awk 'NR==2{printf "%.2f%%", $3*100/$2 }')
  echo "Memory utilization: $MEM_UTILIZATION"
}

storage_utilization () {
  STORAGE_UTILIZATION=$(df -h | awk '$NF=="/"{printf "%s", $5}')
  echo "Storage Utilization: $STORAGE_UTILIZATION"
}

cpu_utilization
memory_utilization
storage_utilization
