while getopts b:d: option
do
case "${option}"
in
b) branch=${OPTARG};;
d) dbdir=${OPTARG};;
esac
done

startbranch=`git branch | grep \* | cut -d ' ' -f2`

if [ -n "$branch" ]; then
  echo "Trying to checkout branch $branch"
  git checkout $branch
else
  branch=$startbranch
  echo "Exeuting on the current branch."
fi

srcdir="../src/Nethermind/Nethermind.PerfTest"
bindir="$srcdir/bin/Release/netcoreapp3.0" 
echo "Source   $srcdir"
echo "Binaries $bindir"
pushd $srcdir

if [ -n "$dbdir" ]; then
  echo "DB dir is $dbdir"
  sed -i -e 's|D\:\\chains\\perftest_ropsten|'$dbdir'|g' Program.cs
else
  echo "Using default DB dir."
fi

sed -i -e 's|Console.ReadLine();||g' Program.cs

dotnet build -c Release
git checkout -- Program.cs
echo "Trying to checkout branch $startbranch"
git checkout $startbranch
popd
pushd $bindir
dotnet Nethermind.PerfTest.dll
popd

echo "Benchmark for $branch complete"
