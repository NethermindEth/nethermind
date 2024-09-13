# Autodoc

*Warning: The following operations with OpenAPI GPT3.5 would cost you around 13$ as of 17/04/2023 and shall cost x10 for GPT4 30k API.*

To generate autodoc:

  1. have OpenAPI key  `<YOUR_OPENAPI_KEY>`; have a `<local_path_where_to_store_data>`
  2. Have docker installed
  3. this will start docker container with `autodoc` preinstalled
```bash
    # in this directory
    docker build -t autodoc -f  Dockerfile.autodoc
    docker run -v <local_path_where_to_store_docs>:/data -e OPENAI_API_KEY=<YOUR_OPENAPI_KEY> -it autodoc
```
  4. in the container run 
```bash
mkdir /test && cd /test
git clone --recursive https://github.com/NethermindEth/nethermind
cd nethermind/
doc init
doc index
7zz a -tzip -mx0 /code_with_docs.zip /test/
cp /code_with_docs.zip  /data/
```
  5. now in the archive you would see the `.autodoc` folder with generated docs and vectors
  
  
  
# Doxygen

For use with doxygen you would also need  to run a script that would link .md files to .cs files. 
Inside our container run:

 ```bash
cd /test/nethermind/tools/autodoc/
python3 /addDoxygenTagsToMdFiles.py --source_dir "/test/nethermind/src/Nethermind/" --md_dir "/test/nethermind/.autodoc/docs/markdown/src/Nethermind"
doxygen Doxyfile
7zz a -tzip -mx0 ./docs_with_graphs.zip ./html/
cp ./docs_with_graphs.zip  /data/
```