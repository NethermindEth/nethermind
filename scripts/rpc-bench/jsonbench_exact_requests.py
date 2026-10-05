"""One explicit, pinned json-bench build patch; no general dirty-source exception."""
import argparse
import hashlib
import json
from pathlib import Path
import stat
import subprocess

BASE = 'de1bcfadea47258ccacae2f420141032a82a9ded'
BASE_TREE = '6377178504604eed92ee3b420b68bf94dc1f4b24'
RESULT_TREE = 'c817e4acf044a6ce317695578a7e045cb19ca528'
PATCH_SHA = 'eecd7d27ed1f6032b6cedc9ca161947cd4c10a80abb379e0d1639af991146284'
FILE = 'runner/generator/scripts/k6-script.js'
BEFORE_SHA = 'd832446b489a28d28b064f31bfbb6ebae2d7f84092cccfedc64c3782058e1da2'
AFTER_SHA = '33c3fc34aba1c78d3801abd8456e55be59f7e1244593ff954363f41f8603a205'
HERE = Path(__file__).resolve().parent
PROTOCOL = 'RPC_EXACT_COUNT_V1'
SOURCE_MANIFEST_SHA = '9133b74ff256fd3184ca8ded296146205f74d2a5f32ffd0f0eab14d29d210e52'


def require(condition, message):
    if not condition: raise ValueError(message)


def file_digest(path):
    before=path.lstat()
    require(path.resolve(strict=True)==path and stat.S_ISREG(before.st_mode) and before.st_nlink==1,'REGULAR_OWNED_FILE_REQUIRED')
    result=hashlib.sha256(path.read_bytes()).hexdigest()
    after=path.lstat()
    fields=('st_dev','st_ino','st_mode','st_nlink','st_size','st_mtime_ns','st_ctime_ns')
    require(tuple(getattr(before,key) for key in fields)==tuple(getattr(after,key) for key in fields),'SOURCE_CHANGED_DURING_HASH')
    return result


def command(arguments):
    child=subprocess.Popen(arguments,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    # A timeout leaves ownership uncertain for the enclosing reviewed HOLD handler; never kill it.
    output,errors=child.communicate(timeout=20)
    require(child.returncode==0 and len(output)<=1024*1024,'PATCH_COMMAND_FAILED')
    return output.decode('utf-8',errors='strict').strip()


def git(source,*arguments):
    return command(['git','-C',str(source),*arguments])


def identity(source):
    require(source.resolve(strict=True)==source and source.is_dir(),'CANONICAL_SOURCE_REQUIRED')
    require(git(source,'rev-parse','HEAD')==BASE and git(source,'rev-parse','HEAD^{tree}')==BASE_TREE,'EXACT_BASE_COMMIT_TREE_REQUIRED')
    git(source,'diff','--no-ext-diff','--quiet','--cached','HEAD','--')


def source_tree(source):
    manifest_path=HERE/'jsonbench-source-manifest.json'
    require(file_digest(manifest_path)==SOURCE_MANIFEST_SHA,'PINNED_SOURCE_MANIFEST_REQUIRED')
    manifest=json.loads(manifest_path.read_bytes())
    files=manifest['files']
    actual={}
    for name,pin in files.items():
        path=source/name
        before=path.lstat()
        require(path.resolve(strict=True)==path and stat.S_ISREG(before.st_mode) and before.st_nlink==1,'REGULAR_TRACKED_SOURCE_REQUIRED')
        require(bool(before.st_mode & 0o111)==(pin['mode']=='100755'),'EXACT_TRACKED_MODE_REQUIRED')
        data=path.read_bytes()
        after=path.lstat()
        fields=('st_dev','st_ino','st_mode','st_nlink','st_size','st_mtime_ns','st_ctime_ns')
        require(tuple(getattr(before,key) for key in fields)==tuple(getattr(after,key) for key in fields),'TRACKED_SOURCE_CHANGED')
        blob=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
        expected='dcb6ad10eb2ba2c0783b1a017073525de40d0034' if name==FILE else pin['blob']
        require(blob==expected,'TRACKED_BUILD_SOURCE_CHANGED')
        actual[name]={'mode':pin['mode'],'blob':blob}
    generated={'rpc-calls/corpus/classes.json','rpc-calls/corpus/class_1.json',
               'rpc-calls/corpus/class_2.json','rpc-calls/corpus/class_3.json'}
    allowed_files=set(files)|generated
    allowed_directories={'rpc-calls','rpc-calls/corpus'}
    for name in allowed_files:
        allowed_directories.update(parent.as_posix() for parent in Path(name).parents if parent.as_posix()!='.')
    import os
    for directory,children,names in os.walk(source,followlinks=False):
        parent=Path(directory)
        if parent==source and '.git' in children:
            metadata=parent/'.git'
            require(metadata.resolve(strict=True)==metadata and metadata.is_dir(),'GIT_METADATA_ALIAS_REFUSED')
            children.remove('.git')
        for name in children:
            path=parent/name;relative=path.relative_to(source).as_posix()
            require(relative in allowed_directories and path.resolve(strict=True)==path,'UNDECLARED_SOURCE_DIRECTORY_REFUSED')
        for name in names:
            path=parent/name;relative=path.relative_to(source).as_posix();info=path.lstat()
            require(relative in allowed_files and path.resolve(strict=True)==path
                    and stat.S_ISREG(info.st_mode) and info.st_nlink==1,'UNDECLARED_SOURCE_FILE_REFUSED')
    context_names={'go.mod','go.sum'}|{name for name in files if name.startswith('runner/')}
    expected_directories={'runner'}
    for name in context_names:
        expected_directories.update(parent.as_posix() for parent in Path(name).parents if str(parent)!='.')
    observed_names={'go.mod','go.sum'};observed_directories={'runner'}
    for path in (source/'runner').rglob('*'):
        require(path.resolve(strict=True)==path,'BUILD_CONTEXT_ALIAS_REFUSED')
        name=path.relative_to(source).as_posix();info=path.lstat()
        if stat.S_ISDIR(info.st_mode):observed_directories.add(name)
        else:
            require(stat.S_ISREG(info.st_mode) and info.st_nlink==1,'BUILD_CONTEXT_SPECIAL_FILE_REFUSED')
            observed_names.add(name)
    require(observed_names==context_names and observed_directories==expected_directories,'UNTRACKED_OR_IGNORED_BUILD_INPUT_REFUSED')
    def tree(prefix):
        entries={}
        for name,pin in actual.items():
            if prefix and not name.startswith(prefix+'/'):continue
            tail=name[len(prefix)+1:] if prefix else name
            leaf,sep,_=tail.partition('/')
            entries[leaf]=('40000',None) if sep else (pin['mode'],pin['blob'])
        payload=b''
        for leaf,(mode,blob) in sorted(entries.items(),key=lambda item:item[0]+('/' if item[1][0]=='40000' else '')):
            blob=tree(prefix+'/'+leaf if prefix else leaf) if mode=='40000' else blob
            payload+=mode.encode()+b' '+leaf.encode()+b'\0'+bytes.fromhex(blob)
        return hashlib.sha1(b'tree '+str(len(payload)).encode()+b'\0'+payload).hexdigest()
    result=tree('')
    require(result==RESULT_TREE,'ACTUAL_PATCHED_TREE_REQUIRED')
    context={name:actual[name] for name in sorted(context_names)}
    return {'actual_result_tree':result,'build_copy_files':len(context),
            'build_context_sha256':hashlib.sha256(json.dumps(context,sort_keys=True,separators=(',',':')).encode()).hexdigest()}


def prepare(source):
    identity(source)
    changed=git(source,'diff','--no-ext-diff','--name-only','HEAD','--')
    if changed:
        return verify(source)
    changed=git(source,'diff','--no-ext-diff','--name-only','HEAD','--')
    if changed:
        return verify(source)
    git(source,'diff','--no-ext-diff','--quiet','HEAD','--')
    require(file_digest(source/FILE)==BEFORE_SHA,'EXACT_BASE_SCRIPT_REQUIRED')
    patch=HERE/'jsonbench-cap.patch'
    require(file_digest(patch)==PATCH_SHA,'EXACT_PATCH_BYTES_REQUIRED')
    git(source,'apply','--check','--',str(patch))
    git(source,'apply','--',str(patch))
    return verify(source)


def verify(source):
    identity(source)
    require(git(source,'diff','--no-ext-diff','--name-only','HEAD','--')==FILE,'ONLY_PINNED_SCRIPT_PATCH_REQUIRED')
    git(source,'diff','--no-ext-diff','--quiet','HEAD','--','.',':(exclude)'+FILE)
    require(file_digest(source/FILE)==AFTER_SHA,'EXACT_RESULT_SCRIPT_REQUIRED')
    require(file_digest(HERE/'jsonbench-cap.patch')==PATCH_SHA,'EXACT_PATCH_BYTES_REQUIRED')
    context=source_tree(source)
    return {'build_context':context,'protocol':PROTOCOL,'base_commit':BASE,'base_tree':BASE_TREE,
            'patch_sha256':PATCH_SHA,'result_tree':RESULT_TREE,'result_file_sha256':AFTER_SHA,
            'tracked_source_clean':False,'exact_pinned_patch_verified':True}


def binary_inventory(output):
    import re
    lines=output.splitlines()
    require(len(lines)==3 and re.match(r'^k6 v2\.1\.0(?:\s|$)',lines[0]),'PINNED_K6_VERSION_REQUIRED')
    inventory={}
    for line in lines[1:]:
        match=re.fullmatch(r'([0-9a-f]{64})  (/[^\s]+)',line)
        require(match is not None,'EXACT_TOOL_BINARY_HASH_REQUIRED')
        path=match[2]
        require(path not in inventory,'DUPLICATE_TOOL_BINARY_PIN')
        inventory[path]=match[1]
    require('/app/jsonrpc-bench-runner' in inventory and len(inventory)==2
            and any(path in inventory for path in ('/usr/bin/k6','/usr/local/bin/k6')),'EXACT_TOOL_BINARY_PATHS_REQUIRED')
    return {'k6_version':lines[0],'binaries_sha256':inventory}


def validate_config(config, phase, cap):
    require(type(cap) is int and (phase,cap) in {('warm',6000),('main',1000),('main',20000)},'DECLARED_REQUEST_CAP_REQUIRED')
    duration,seed={6000:('60s',1001),1000:('10s',1),20000:('200s',1)}[cap]
    require(type(config.get('rps')) is int and config['rps']==100
            and type(config.get('vus')) is int and config['vus']==100
            and type(config.get('seed')) is int and config['seed']==seed
            and config.get('duration')==duration,'FROZEN_RATE_DURATION_SEED_VUS_REQUIRED')
    require(isinstance(config.get('clients'),list) and len(config['clients'])==1,'ONE_SCENARIO_REQUIRED')
    return cap


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('operation',choices=['prepare','verify','config'])
    parser.add_argument('--source',type=Path);parser.add_argument('--config',type=Path)
    parser.add_argument('--phase');parser.add_argument('--cap',type=int);args=parser.parse_args()
    if args.operation=='config':
        import yaml
        print(validate_config(yaml.safe_load(args.config.read_text()),args.phase,args.cap))
    else:
        print(json.dumps(prepare(args.source.absolute()) if args.operation=='prepare' else verify(args.source.absolute()),sort_keys=True))
