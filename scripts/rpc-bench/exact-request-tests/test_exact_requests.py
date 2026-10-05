import hashlib,json,subprocess,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
import jsonbench_exact_requests as exact
HERE=Path(__file__).resolve().parent
BEFORE=HERE/'before/runner/generator/scripts/k6-script.js'
AFTER=HERE/'after/runner/generator/scripts/k6-script.js'


def execute(script,cap,indices):
    data=json.dumps({'script':str(script),'cap':cap,'indices':indices}).encode()
    value=subprocess.run(['node',str(HERE/'execute_script_stub.js')],input=data,stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True)
    return json.loads(value.stdout)


class ExactRequestTests(unittest.TestCase):
    def test_original_deadline_extra_posts_then_patched_script_submits_exact_count(self):
        for limit in (1000,6000,20000):
            with self.subTest(limit=limit):
                indices=list(range(limit+1))
                before=execute(BEFORE,str(limit),indices);after=execute(AFTER,str(limit),indices)
                self.assertEqual(before['count'],limit+1)
                self.assertEqual(after['count'],limit);self.assertEqual(after['checks'],limit*2)
                self.assertEqual(after['posted'],list(range(limit)));self.assertIsNone(after['error'])

    def test_global_across_vus_cap_and_boundaries_do_not_post(self):
        indices=[0,99,100,5999,6000,6001,2147483647]
        result=execute(AFTER,'6000',indices)
        self.assertEqual(result['posted'],[0,99,100,5999]);self.assertIsNone(result['error'])

    def test_drops_are_never_filled_in_or_replayed(self):
        result=execute(AFTER,'6000',list(range(5988)))
        self.assertEqual(result['count'],5988);self.assertEqual(len(set(result['posted'])),5988)
        self.assertLess(result['count'],6000)

    def test_malformed_cap_refuses_before_http(self):
        for value in (None,'','0','-1','1.5','06000','NaN','Infinity','9007199254740992','6e3','6000 '):
            with self.subTest(value=value):
                result=execute(AFTER,value,[0]);self.assertEqual(result['count'],0)
                self.assertEqual(result['error'],'Invalid RPC_GLOBAL_REQUEST_CAP')

    def test_config_keeps_counts_rate_duration_seed_and_fixed_100_vus(self):
        for phase,cap,duration,seed in [('warm',6000,'60s',1001),('main',1000,'10s',1),('main',20000,'200s',1)]:
            cfg={'rps':100,'vus':100,'duration':duration,'seed':seed,'clients':['synthetic']}
            self.assertEqual(exact.validate_config(cfg,phase,cap),cap)
            for key,value in [('rps',101),('rps',True),('vus',10),('vus',101),('seed',0),('duration','61s'),('clients',[]),('clients',['A','B'])]:
                with self.subTest(phase=phase,cap=cap,key=key),self.assertRaises(ValueError):exact.validate_config(dict(cfg,**{key:value}),phase,cap)
        for phase,cap in [('warm',6001),('main',True),('main',6000),('main',0),('other',1000)]:
            with self.subTest(phase=phase,cap=cap),self.assertRaises(ValueError):exact.validate_config({},phase,cap)

    def test_exact_source_result_and_wrong_base_or_extra_dirty_file_refuse(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve();script=root/exact.FILE;script.parent.mkdir(parents=True);script.write_bytes(AFTER.read_bytes())
            calls=[]
            def git(_root,*args):
                calls.append(args)
                if args==('rev-parse','HEAD'):return exact.BASE
                if args==('rev-parse','HEAD^{tree}'):return exact.BASE_TREE
                if '--name-only' in args:return exact.FILE
                return ''
            with patch.object(exact,'git',side_effect=git),patch.object(exact,'source_tree',return_value={'actual_result_tree':exact.RESULT_TREE,'build_copy_files':65,'build_context_sha256':'c'*64}):
                record=exact.verify(root);self.assertFalse(record['tracked_source_clean']);self.assertTrue(record['exact_pinned_patch_verified'])
                self.assertEqual(record['result_tree'],exact.RESULT_TREE)
                # Existing exact result is recognized; never replay git apply or broadly reset.
                self.assertEqual(exact.prepare(root),record)
                self.assertFalse(any('apply' in args for args in calls))
            for bad in [lambda _root,*args:'b'*40 if args==('rev-parse','HEAD') else '',
                        lambda _root,*args: exact.BASE if args==('rev-parse','HEAD') else exact.BASE_TREE if args==('rev-parse','HEAD^{tree}') else exact.FILE+'\nother.go' if '--name-only' in args else '']:
                with patch.object(exact,'git',side_effect=bad),self.assertRaises(ValueError):exact.verify(root)
            script.write_bytes(AFTER.read_bytes()+b'\n')
            with patch.object(exact,'git',side_effect=git),self.assertRaises(ValueError):exact.verify(root)

    def test_base_patch_hash_and_apply_order_without_git_mutation(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve();script=root/exact.FILE;script.parent.mkdir(parents=True);script.write_bytes(BEFORE.read_bytes())
            applied=[]
            def git(_root,*args):
                if args==('rev-parse','HEAD'):return exact.BASE
                if args==('rev-parse','HEAD^{tree}'):return exact.BASE_TREE
                if '--name-only' in args:return exact.FILE if applied else ''
                if args[:2]==('apply','--check'):return ''
                if args[0]=='apply':applied.append(args);script.write_bytes(AFTER.read_bytes())
                return ''
            with patch.object(exact,'git',side_effect=git),patch.object(exact,'source_tree',return_value={'actual_result_tree':exact.RESULT_TREE,'build_copy_files':65,'build_context_sha256':'c'*64}):self.assertEqual(exact.prepare(root)['protocol'],exact.PROTOCOL)
            self.assertEqual(len(applied),1)
            with patch.object(exact,'PATCH_SHA','0'*64),patch.object(exact,'git',side_effect=git),self.assertRaises(ValueError):exact.verify(root)

    def test_actual_tree_reconstruction_and_extra_ignored_context_input_refuse(self):
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory).resolve();root=base/'src';root.mkdir()
            data={'go.mod':b'module synthetic\n','go.sum':b'synthetic checksum\n',exact.FILE:AFTER.read_bytes()}
            manifest={'files':{}}
            for name,value in data.items():
                path=root/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(value)
                manifest['files'][name]={'mode':'100644','blob':hashlib.sha1(b'blob '+str(len(value)).encode()+b'\0'+value).hexdigest()}
            def tree(prefix=''):
                entries={}
                for name,pin in manifest['files'].items():
                    if prefix and not name.startswith(prefix+'/'):continue
                    tail=name[len(prefix)+1:] if prefix else name;leaf,sep,_=tail.partition('/')
                    entries[leaf]=('40000',None) if sep else (pin['mode'],pin['blob'])
                payload=b''
                for leaf,(mode,blob) in sorted(entries.items(),key=lambda item:item[0]+('/' if item[1][0]=='40000' else '')):
                    blob=tree(prefix+'/'+leaf if prefix else leaf) if mode=='40000' else blob
                    payload+=mode.encode()+b' '+leaf.encode()+b'\0'+bytes.fromhex(blob)
                return hashlib.sha1(b'tree '+str(len(payload)).encode()+b'\0'+payload).hexdigest()
            package=base/'package';package.mkdir();pin=package/'jsonbench-source-manifest.json';pin.write_bytes(json.dumps(manifest).encode())
            with patch.object(exact,'HERE',package),patch.object(exact,'SOURCE_MANIFEST_SHA',hashlib.sha256(pin.read_bytes()).hexdigest()),patch.object(exact,'RESULT_TREE',tree()):
                observed=exact.source_tree(root);self.assertEqual(observed['actual_result_tree'],tree())
                self.assertEqual(observed['build_copy_files'],3)
                # A fixture outside COPY paths is allowed, but no injected/ignored Go source is.
                fixture=root/'rpc-calls/corpus/class_1.json';fixture.parent.mkdir(parents=True);fixture.write_bytes(b'synthetic private fixture')
                self.assertEqual(exact.source_tree(root),observed)
                extra=root/'runner/ignored.go';extra.write_bytes(b'package injected')
                with self.assertRaises(ValueError):exact.source_tree(root)
                extra.unlink()
                for name in ('.dockerignore','runner/Dockerfile.dockerignore','rpc-calls/corpus/unexpected.json'):
                    extra=root/name;extra.write_bytes(b'undeclared')
                    with self.subTest(name=name),self.assertRaises(ValueError):exact.source_tree(root)
                    extra.unlink()
                (root/'go.mod').write_bytes(b'module corrupt')
                with self.assertRaises(ValueError):exact.source_tree(root)

    def test_binary_inventory_requires_actual_two_pins_and_exact_k6(self):
        valid='k6 v2.1.0 (synthetic)\n'+'a'*64+'  /app/jsonrpc-bench-runner\n'+'b'*64+'  /usr/bin/k6'
        self.assertEqual(len(exact.binary_inventory(valid)['binaries_sha256']),2)
        for value in [valid.replace('v2.1.0','v2.2.0'),valid.replace('/usr/bin/k6','/tmp/k6'),valid.replace('a'*64,'a'*63),valid+'\nextra',valid.replace('/usr/bin/k6','/app/jsonrpc-bench-runner')]:
            with self.subTest(value=value),self.assertRaises(ValueError):exact.binary_inventory(value)


if __name__=='__main__':unittest.main(verbosity=2)
