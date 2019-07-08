times=$1

cp benchmark.sh copyof.benchmark.sh
cp commits.list copyof.commits.list

while read commit; do
  echo "Commit $commit"
  COUNTER=0
  while [  $COUNTER -lt $times ];
  do
    echo "Running $commit.$COUNTER.bench"
    #./copyof.benchmark.sh -d "C:/perf_db" -b $commit > result.bench
    #grep "TOTAL after 100000" result.bench > $commit.$COUNTER.bench
    #grep Mgas/s $commit.$COUNTER.bench
    let COUNTER=$COUNTER+1
  done
done <copyof.commits.list

rm copyof.benchmark.sh
rm copyof.commits.list
