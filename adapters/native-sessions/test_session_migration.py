import importlib.util
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('session_migration',Path(__file__).with_name('session_migration.py'))
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MigrationTests(unittest.TestCase):
    def fixture(self, home):
        (home/'sessions').mkdir()
        (home/'config.toml').write_text('model_provider="openai"\nmodel="gpt-6.1-sol"\nmodel_reasoning_effort="xhigh"\n',encoding='utf-8')
        db=sqlite3.connect(home/'state_5.sqlite')
        db.execute('CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT,source TEXT,model_provider TEXT,model TEXT,reasoning_effort TEXT)')
        for identifier,source in [('one','vscode'),('two','vscode'),('child','subAgentThreadSpawn'),('missing','vscode')]:
            path=home/'sessions'/(identifier+'.jsonl')
            if identifier!='missing':
                header=json.dumps({'type':'session_meta','payload':{'id':identifier,'model_provider':'third_party_114514'}}).encode()+b'\r\n'
                path.write_bytes(header+b'{"original":"history bytes stay exactly as written"}\n')
            db.execute('INSERT INTO threads VALUES(?,?,?,?,?,?)',(identifier,str(path),source,'third_party_114514','old-model','max'))
        db.commit();db.close()

    def test_apply_preserves_history_offsets_child_and_restore_material(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary);self.fixture(home)
            original={p.name:p.read_bytes() for p in (home/'sessions').glob('*.jsonl')}
            plan=module.migrate(home)
            self.assertEqual(plan['headersToUpdate'],2)
            self.assertEqual(original,{p.name:p.read_bytes() for p in (home/'sessions').glob('*.jsonl')})
            result=module.migrate(home,True)
            self.assertEqual(result['existingMissingHistoryFiles'],1)
            for name in ['one.jsonl','two.jsonl']:
                after=(home/'sessions'/name).read_bytes()
                self.assertEqual(len(after),len(original[name]))
                self.assertEqual(after.split(b'\n',1)[1],original[name].split(b'\n',1)[1])
                self.assertEqual(json.loads(after.split(b'\n',1)[0])['payload']['model_provider'],'openai')
            self.assertEqual((home/'sessions/child.jsonl').read_bytes(),original['child.jsonl'])
            manifest=json.loads((Path(result['backup'])/'manifest.json').read_text(encoding='utf-8'))
            self.assertEqual(len(manifest['headers']),2)
            self.assertEqual((Path(result['backup'])/'header-0.bin').read_bytes(),original['one.jsonl'].split(b'\n',1)[0]+b'\n')
            self.assertFalse(module.migrate(home,True,True)['reloadRequired'])

    def test_later_write_failure_rolls_back_files_and_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary);self.fixture(home)
            original={p.name:p.read_bytes() for p in (home/'sessions').glob('*.jsonl')}
            real_write=module.write_header;count=0
            def fail_second(stream,value):
                nonlocal count
                count+=1
                if count==2:raise OSError('injected write failure')
                real_write(stream,value)
            with patch.object(module,'write_header',fail_second):
                with self.assertRaises(OSError):module.migrate(home,True)
            self.assertEqual(original,{p.name:p.read_bytes() for p in (home/'sessions').glob('*.jsonl')})
            with closing(sqlite3.connect(home/'state_5.sqlite')) as db:
                self.assertEqual(db.execute("SELECT count(*) FROM threads WHERE model_provider='openai'").fetchone()[0],0)

    def test_wrong_session_metadata_rejects_entire_batch(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary);self.fixture(home)
            (home/'sessions/two.jsonl').write_text('{}\n',encoding='utf-8')
            before=(home/'sessions/one.jsonl').read_bytes()
            with self.assertRaises(ValueError):module.migrate(home,True)
            self.assertEqual((home/'sessions/one.jsonl').read_bytes(),before)


if __name__=='__main__':unittest.main()
