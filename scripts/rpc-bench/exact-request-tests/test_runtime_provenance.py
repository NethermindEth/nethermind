import ast,contextlib,json,os,re,sys,tempfile,types,unittest
from pathlib import Path
from unittest.mock import Mock,patch
import jsonbench_exact_requests as exact
HERE=Path(__file__).resolve().parent


def functions():
    source=HERE.parent/'private_audit.py'
    tree=ast.parse(source.read_text(encoding='utf-8'))
    selected=ast.Module(body=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('runtime_pin','tool_version')],type_ignores=[])
    space={'OWNER_LABEL':'synthetic.rpc.owner','os':os,'Path':Path,'re':re,'json':json,'hashlib':__import__('hashlib'),'time':__import__('time')}
    exec(compile(selected,str(source),'exec'),space)
    return space


class RuntimeProvenanceTests(unittest.TestCase):
    def test_new_protocol_is_explicit_and_cross_phase_binary_identity_fails_closed(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder).resolve();label='nativecap';state=root/'state';scratch=root/'scratch'
            preparation={'expected_counts':{'warm':6000,'main':1000},'roots':{'SCRATCH_ROOT':str(scratch),'STATE_ROOT':str(state)}}
            space=functions()
            space.update(context=lambda:(root,None,preparation),arm_directory=lambda r,l:r/'outputs'/l,
              archive_tool=types.SimpleNamespace(private_directory=lambda p:p.mkdir(parents=True,exist_ok=True)),
              owned_cid=lambda *a:'c'*64,file_pin=lambda p:{'sha256':'f'*64,'bytes':100},
              snapshot_head=lambda *a:{'number':25490000,'hash':'0x'+'a'*64},
              write=lambda p,v:p.write_text(json.dumps(v),encoding='utf-8'))
            node={'Id':'c'*64,'Name':'/rpcbench-sweep-nativecap-123','Image':'sha256:'+'b'*64,'Config':{'Image':'nethermindeth/nethermind@sha256:'+'e'*64},'State':{'Running':True}}
            space['command_output']=lambda a:'sha256:'+'a'*64 if a[:3]==['docker','image','inspect'] else json.dumps(node)
            binaries={'/app/jsonrpc-bench-runner':'1'*64,'/usr/bin/k6':'2'*64}
            space['tool_version']=lambda *a:{'k6_version':'k6 v2.1.0 (synthetic)','binaries_sha256':dict(binaries)}
            record={'protocol':exact.PROTOCOL,'base_commit':exact.BASE,'base_tree':exact.BASE_TREE,'result_tree':exact.RESULT_TREE,
                    'patch_sha256':exact.PATCH_SHA,'tracked_source_clean':False,'exact_pinned_patch_verified':True,
                    'build_context':{'actual_result_tree':exact.RESULT_TREE,'build_copy_files':100,'build_context_sha256':'d'*64}}
            env={'LABEL':label,'RPC_PRIVATE_AUDIT_PHASE':'warm','GITHUB_RUN_ID':'123','JB_REF':exact.BASE,
                 'RPC_JB_PATCH_PROTOCOL':exact.PROTOCOL,'JB_ETH_CALL_CORPUS_FILE':str(root/'synthetic.gz'),
                 'RPC_URL':'http://127.0.0.1:8545','SNAPSHOT_BLOCK':'25490000','JB_EXACT_REQUEST_CAP':'6000'}
            config_file=scratch/'jsonbench/io/benchmark.yaml';config_file.parent.mkdir(parents=True)
            config_file.write_text(json.dumps({'rps':100,'vus':100,'duration':'60s','seed':1001,'clients':['nativecap']}))
            with patch.dict(os.environ,env,clear=True),patch.object(exact,'verify',return_value=record):
                self.assertEqual(space['runtime_pin']('synthetic',exact.BASE,'true'),'sha256:'+'a'*64)
                warm=json.loads((root/'outputs/nativecap/warm/runtime-pin.json').read_text())
                self.assertFalse(warm['tool']['tracked_source_clean']);self.assertTrue(warm['tool']['source_patch']['exact_pinned_patch_verified'])
                self.assertEqual(warm['tool']['binaries_sha256'],binaries)
                self.assertEqual(warm['request_contract']['vus'],100);self.assertEqual(warm['request_contract']['requests'],6000)
                os.environ['RPC_PRIVATE_AUDIT_PHASE']='main';os.environ['JB_EXACT_REQUEST_CAP']='1000'
                config_file.write_text(json.dumps({'rps':100,'vus':100,'duration':'10s','seed':1,'clients':['nativecap']}))
                self.assertEqual(space['runtime_pin']('synthetic',exact.BASE,'true'),'sha256:'+'a'*64)
                (root/'outputs/nativecap/main/runtime-pin-complete.json').unlink()
                binaries['/app/jsonrpc-bench-runner']='3'*64
                with self.assertRaisesRegex(ValueError,'CROSS_CELL_PROVENANCE_MISMATCH'):space['runtime_pin']('synthetic',exact.BASE,'true')
                self.assertFalse((root/'outputs/nativecap/main/runtime-pin-complete.json').exists())
                os.environ['RPC_JB_PATCH_PROTOCOL']='unknown'
                with self.assertRaisesRegex(ValueError,'EXACT_TOOL_PATCH_PROTOCOL_REQUIRED'):space['runtime_pin']('synthetic',exact.BASE,'true')

    def test_owned_readonly_version_container_hashes_before_load(self):
        space=functions();commands=[];stopped=[]
        def output(args):
            commands.append(args)
            if args[1]=='start':return 'k6 v2.1.0 (synthetic)\n'+'1'*64+'  /app/jsonrpc-bench-runner\n'+'2'*64+'  /usr/bin/k6'
            if args[1]=='inspect':return '0'
            return ''
        space.update(run_identity=lambda:'123-1',register_container=lambda *a:Path('/synthetic/cid'),
                     owned_cid=lambda *a:'c'*64,command_output=output,stop_owned_tool=lambda *a:stopped.append(a))
        with patch.dict(os.environ,{'LABEL':'nativecap','RPC_PRIVATE_AUDIT_PHASE':'warm','RPC_JB_PATCH_PROTOCOL':exact.PROTOCOL},clear=True):
            result=space['tool_version']('sha256:'+'a'*64,Path('/synthetic'))
        self.assertEqual(result['binaries_sha256']['/app/jsonrpc-bench-runner'],'1'*64)
        create=commands[0];self.assertIn('--read-only',create);self.assertEqual(create[create.index('--network')+1],'none')
        self.assertIn('sha256sum',create[-1]);self.assertEqual(len(stopped),1)

    def test_timeout_never_signals_spawned_helper(self):
        import subprocess
        child=Mock();child.communicate.side_effect=subprocess.TimeoutExpired('synthetic',20)
        with patch.object(exact.subprocess,'Popen',return_value=child),self.assertRaises(subprocess.TimeoutExpired):exact.command(['synthetic'])
        child.kill.assert_not_called();child.terminate.assert_not_called();child.send_signal.assert_not_called()


if __name__=='__main__':unittest.main(verbosity=2)
