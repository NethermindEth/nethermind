#iex ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))
#$Env:path = $env:path + ";%ALLUSERSPROFILE%\chocolatey\bin"
#choco install wget

cd ..

#rm -rf rocksdb
#rm -r gflags-2.2.0

#git clone https://github.com/facebook/rocksdb.git
#cd rocksdb
#git checkout tags/v5.15.10
#cd ..

#wget https://github.com/gflags/gflags/archive/v2.2.0.zip
#unzip v2.2.0.zip
#rm v2.2.0.zip
#cd gflags-2.2.0
#mkdir target
#cmake -G "Visual Studio 14 Win64"

#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" gflags.sln //p:Configuration=Debug //p:Platform=x64
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" gflags.sln //p:Configuration=Release //p:Platform=x64
#cd ..

#rm -rf snappy-visual-cpp
#hg clone https://bitbucket.org/robertvazan/snappy-visual-cpp
#cd snappy-visual-cpp
#hg diff --reverse -r 44:45 > exports.patch
#hg import exports.patch --no-commit
#"C:/Program Files (x86)/Microsoft Visual Studio 14.0/Common7/IDE/devenv.exe" snappy.sln //upgrade
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" snappy.sln //p:Configuration=Debug //p:Platform=x64
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" snappy.sln //p:Configuration=Release //p:Platform=x64
#cd ..

#rm -rf lz4-1.7.5
#wget https://github.com/lz4/lz4/archive/v1.7.5.zip
#unzip v1.7.5.zip
#rm v1.7.5.zip
#cd lz4-1.7.5
#cd visual/VS2010
#"C:/Program Files (x86)/Microsoft Visual Studio 14.0/Common7/IDE/devenv.exe" lz4.sln //upgrade
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" lz4.sln //p:Configuration=Debug //p:Platform=x64
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" lz4.sln //p:Configuration=Release //p:Platform=x64
#cd ../..

#rm -rf zlib-1.2.11
#wget http://zlib.net/zlib1211.zip
#unzip zlib1211.zip
#rm zlib1211.zip
#cd zlib-1.2.11/contrib/vstudio/vc14
#sed -i 's/<Command>cd \.\.\\\.\.\\contrib\\masmx64/<Command>cd \.\.\\\.\.\\masmx64/g' zlibvc.vcxproj
#"C:/Program Files (x86)/Microsoft Visual Studio 14.0/VC/bin/amd64_x86/vcvarsamd64_x86.bat"
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" zlibvc.sln //p:Configuration=Debug //p:Platform=x64
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" zlibvc.sln //p:Configuration=Release //p:Platform=x64
#cp x64/ZlibDllDebug/zlibwapi.lib x64/ZlibStatDebug/
#cp x64/ZlibDllRelease/zlibwapi.lib x64/ZlibStatRelease/
#cd ../../../..

cd rocksdb
sed -i 's/Gflags\.Library/gflags-2\.2\.0/g' thirdparty.inc
sed -i 's/\${GFLAGS_HOME}\/build\/native\/include/\${GFLAGS_HOME}\/target\/include/g' thirdparty.inc
sed -i 's/\${GFLAGS_HOME}\/lib\/native\/debug\/amd64\/gflags\.lib/${GFLAGS_HOME}\/target\/lib\/Debug\/gflags_static.lib/g' thirdparty.inc
sed -i 's/\${GFLAGS_HOME}\/lib\/native\/retail\/amd64\/gflags\.lib/${GFLAGS_HOME}\/target\/lib\/Release\/gflags_static.lib/g' thirdparty.inc

sed -i 's/Snappy\.Library/snappy-visual-cpp/g' thirdparty.inc
sed -i 's/\${SNAPPY_HOME}\/build\/native\/inc\/inc/\${SNAPPY_HOME}/g' thirdparty.inc
sed -i 's/\${SNAPPY_HOME}\/lib\/native\/debug\/amd64\/snappy\.lib/\${SNAPPY_HOME}\/x64\/Debug\/snappy64\.lib/g' thirdparty.inc
sed -i 's/\${SNAPPY_HOME}\/lib\/native\/retail\/amd64\/snappy\.lib/\${SNAPPY_HOME}\/x64\/Release\/snappy64\.lib/g' thirdparty.inc

sed -i 's/LZ4\.Library/lz4-1\.7\.5/g' thirdparty.inc
sed -i 's/\${LZ4_HOME}\/build\/native\/inc\/inc/\${LZ4_HOME}\/lib/g' thirdparty.inc
#sed -i 's///g' thirdparty.inc
#sed -i 's///g' thirdparty.inc

#sed -i 's///g' thirdparty.inc
#sed -i 's///g' thirdparty.inc
#sed -i 's///g' thirdparty.inc
#sed -i 's///g' thirdparty.inc

#mkdir build
#cd build
#set THIRDPARTY_HOME=C:/src/
#cmake -G "Visual Studio 14 Win64" -DJNI=1 -DGFLAGS=1 -DSNAPPY=1 -DLZ4=1 -DZLIB=1 -DXPRESS=1 ..
#"C:/Program Files (x86)/MSBuild/14.0/Bin/msbuild.exe" rocksdb.sln //p:Configuration=Release //p:Platform=x64

