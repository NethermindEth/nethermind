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
  echo "Branch is $branch"
  git checkout $branch
else
  echo "Exeuting on the current branch."
fi

if [ -n "$dbdir" ]; then
  echo "DB dir is $dbdir"
else
  echo "Using default DB dir."
  dbdir="D:\\chains\\perftest_ropsten"
fi

srcdir="../src/Nethermind/Nethermind.PerfTest"
bindir="$srcdir/bin/Release/netcoreapp2.2" 
echo "Source   $srcdir"
echo "Binaries $bindir"
pushd $srcdir
sed -i -e 's|D\:\\chains\\perftest_ropsten|'$dbdir'|g' Program.cs
dotnet build -c Release
git checkout -- Program.cs
git checkout $startbranch
popd
pushd $bindir
otnet Nethermind.PerfTest.dll
popd

