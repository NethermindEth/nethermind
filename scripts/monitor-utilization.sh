#!/bin/bash     

cpu_utilization () {
  CPU_UTILIZATION=$(top -bn1 | grep "Cpu(s)" | sed "s/.*, *\([0-9.]*\)%* id.*/\1/" | awk '{print 100 - $1""}')
  CPU_UTILIZATION=$( printf "%.0f" $CPU_UTILIZATION )
  echo "CPU utilization: $CPU_UTILIZATION%"

  if [ $CPU_UTILIZATION -gt 90 ]; then
    echo "CPU utilization is greater than 90%, restarting the service"
    systemctl restart nethermind.service
  fi
}

memory_utilization (){
  MEM_UTILIZATION=$(free -m | awk 'NR==2{printf "%.2f", $3*100/$2 }')
  MEM_UTILIZATION=$( printf "%.0f" $MEM_UTILIZATION )
  echo "Memory utilization: $MEM_UTILIZATION%"

  if [ $MEM_UTILIZATION -gt 90 ]; then
    echo "Memory utilization is greater than 90%, restarting the service"
    systemctl restart nethermind.service
  fi
}

storage_utilization () {
  STORAGE_UTILIZATION=$(df -h | awk '$NF=="/"{printf "%s", $5}')
  STORAGE_UTILIZATION=${STORAGE_UTILIZATION%?}
  echo "Storage Utilization: $STORAGE_UTILIZATION%"

  if [ $STORAGE_UTILIZATION -gt 90 ]; then
    echo "Storage utilization is greater than 90%, restarting the service"
    systemctl restart nethermind.service
  fi
}

cpu_utilization
memory_utilization
storage_utilization
