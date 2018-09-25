cd ..

rm -rf rocksdb
rm -rf gflags-2.2.0
rm -rf snappy-visual-cpp
rm -rf zlib-1.2.11
rm -rf lz4-1.7.5

git clone https://github.com/facebook/rocksdb.git
cd rocksdb
git checkout tags/v5.15.10
cd ..

wget https://github.com/gflags/gflags/archive/v2.2.0.zip
unzip v2.2.0.zip
rm v2.2.0.zip
cd gflags-2.2.0
mkdir target
cd target
cmake -G "Visual Studio 14 Win64" ..
"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" gflags.sln //p:Configuration=Debug //p:Platform=x64
"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" gflags.sln //p:Configuration=Release //p:Platform=x64
cd ../..

hg clone https://bitbucket.org/robertvazan/snappy-visual-cpp
cd snappy-visual-cpp
hg diff --reverse -r 44:45 > exports.patch
hg import exports.patch --no-commit
"C:/Program Files (x86)/Microsoft Visual Studio 14.0/Common7/IDE/devenv.exe" snappy.sln //upgrade
"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" snappy.sln //p:Configuration=Debug //p:Platform=x64
"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" snappy.sln //p:Configuration=Release //p:Platform=x64
cd ..

wget https://github.com/lz4/lz4/archive/v1.7.5.zip
unzip v1.7.5.zip
rm v1.7.5.zip
cd lz4-1.7.5/visual/VS2010
"C:/Program Files (x86)/Microsoft Visual Studio 14.0/Common7/IDE/devenv.exe" lz4.sln //upgrade
"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" lz4.sln //p:Configuration=Debug //p:Platform=x64
"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" lz4.sln //p:Configuration=Release //p:Platform=x64
cd ../../..

wget http://zlib.net/zlib1211.zip
unzip zlib1211.zip
rm zlib1211.zip
cd zlib-1.2.11/contrib/vstudio/vc14
sed -i 's/<Command>cd \.\.\\\.\.\\contrib\\masmx64/<Command>cd \.\.\\\.\.\\masmx64/g' zlibvc.vcxproj
"C:/Program Files (x86)/Microsoft Visual Studio 14.0/VC/bin/amd64_x86/vcvarsamd64_x86.bat"
"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" zlibvc.sln //p:Configuration=Debug //p:Platform=x64
"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" zlibvc.sln //p:Configuration=Release //p:Platform=x64
cp x64/ZlibDllDebug/zlibwapi.lib x64/ZlibStatDebug/
cp x64/ZlibDllRelease/zlibwapi.lib x64/ZlibStatRelease/
cd ../../../..

cd rocksdb
sed -i 's/\/MD/\/MT/g' CMakeLists.txt

sed -i 's/Gflags\.Library/gflags-2.2.0/g' thirdparty.inc
sed -i 's/\${GFLAGS_HOME}\/build\/native\/include/\${GFLAGS_HOME}\/target\/include/g' thirdparty.inc
sed -i 's/\${GFLAGS_HOME}\/lib\/native\/debug\/amd64\/gflags\.lib/${GFLAGS_HOME}\/target\/lib\/Debug\/gflags_static.lib/g' thirdparty.inc
sed -i 's/\${GFLAGS_HOME}\/lib\/native\/retail\/amd64\/gflags\.lib/${GFLAGS_HOME}\/target\/lib\/Release\/gflags_static.lib/g' thirdparty.inc

sed -i 's/Snappy\.Library/snappy-visual-cpp/g' thirdparty.inc
sed -i 's/\${SNAPPY_HOME}\/build\/native\/inc\/inc/\${SNAPPY_HOME}/g' thirdparty.inc
sed -i 's/\${SNAPPY_HOME}\/lib\/native\/debug\/amd64\/snappy\.lib/\${SNAPPY_HOME}\/x64\/Debug\/snappy64.lib/g' thirdparty.inc
sed -i 's/\${SNAPPY_HOME}\/lib\/native\/retail\/amd64\/snappy\.lib/\${SNAPPY_HOME}\/x64\/Release\/snappy64.lib/g' thirdparty.inc

sed -i 's/LZ4\.Library/lz4-1.7.5/g' thirdparty.inc
sed -i 's/\${LZ4_HOME}\/build\/native\/inc\/inc/\${LZ4_HOME}\/lib/g' thirdparty.inc
sed -i 's/\${LZ4_HOME}\/lib\/native\/debug\/amd64\/lz4.lib/\${LZ4_HOME}\/visual\/VS2010\/bin\/x64_Debug\/liblz4_static.lib/g' thirdparty.inc
sed -i 's/\${LZ4_HOME}\/lib\/native\/retail\/amd64\/lz4.lib/\${LZ4_HOME}\/visual\/VS2010\/bin\/x64_Release\/liblz4_static.lib/g' thirdparty.inc

sed -i 's/ZLIB.Library/zlib-1.2.11/g' thirdparty.inc
sed -i 's/\${ZLIB_HOME}\/build\/native\/inc\/inc/\${ZLIB_HOME}/g' thirdparty.inc
sed -i 's/\${ZLIB_HOME}\/lib\/native\/debug\/amd64\/zlib\.lib/\${ZLIB_HOME}\/contrib\/vstudio\/vc14\/x64\/ZlibStatDebug\/zlibwapi.lib/g' thirdparty.inc
sed -i 's/\${ZLIB_HOME}\/lib\/native\/retail\/amd64\/zlib\.lib/\${ZLIB_HOME}\/contrib\/vstudio\/vc14\/x64\/ZlibStatRelease\/zlibwapi.lib/g' thirdparty.inc

mkdir build
cd build
export THIRDPARTY_HOME=C:/src/
cmake -G "Visual Studio 15 2017 Win64" -DOPTDBG=1 -DPORTABLE=0 -DWITH_MD_LIBRARY=0 -DWITH_AVX2=1 -DWITH_JNI=0 -DWITH_GFLAGS=0 -DWITH_SNAPPY=1 -DWITH_LZ4=1 -DWITH_ZLIB=1 -DWITH_XPRESS=0 ..
/bin/find . -type f -name '*.vcxproj' -exec sed -i 's/MultiThreadedDLL/MultiThreaded/g; s/MultiThreadedDebugDLL/MultiThreadedDebug/g' '{}' ';'
#cmake -G "Visual Studio 15 Win64" -DCMAKE_SYSTEM_VERSION=10.0.10240 -DJNI=1 -DGFLAGS=1 -DSNAPPY=1 -DLZ4=1 -DZLIB=1 -DXPRESS=0 ..
#cmake -G "Visual Studio 14 Win64" -DJNI=1 -DGFLAGS=1 -DSNAPPY=1 -DLZ4=1 -DZLIB=1 -DXPRESS=1 ..

"C:/Program Files (x86)/Microsoft Visual Studio/2017/Community/MSBuild/15.0/Bin/msbuild.exe" //m rocksdb.sln //p:Configuration=Release //p:Platform=x64
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" //m rocksdb.sln //p:Configuration=Release //p:Platform=x64
cd ../..