while getopts b:d: option
do
case "${option}"
in
b) branch=${OPTARG};;
d) dbdir=${OPTARG};;
esac
done

if [ -n "$branch" ]; then
  echo "Branch is $branch"
else
  echo "Branch is master."
  branch=master
fi

if [ -n "$dbdir" ]; then
  echo "DB dir is $dbdir"
else
  echo "Using default DB dir."
  dbdir="D:\\chains\\perftest_ropsten"
fi

git checkout $branch

srcdir="../src/Nethermind/Nethermind.PerfTest"
bindir="$srcdir/bin/Release/netcoreapp2.2" 
echo "Source   $srcdir"
echo "Binaries $bindir"
pushd $srcdir
sed -i -e "s/D\:\\chains\\perftest_ropsten/$dbdir/g" Program.cs
dotnet build -c Release
popd
pushd $bindir
dotnet Nethermind.PerfTest.dll
popd
