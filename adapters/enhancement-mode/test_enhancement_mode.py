import importlib.util,json,pathlib,tempfile,tomllib,unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('enhancement_mode',pathlib.Path(__file__).with_name('enhancement_mode.py'))
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class ModeTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='codex-mode-中文-');self.root=pathlib.Path(self.temp.name);self.home=self.root/'home';self.home.mkdir()
        self.manager=module.ModeManager(self.home,self.root,self.root/'state')
        self.config='''# user configuration\nmodel_provider = "openai"\nmodel = "gpt-6.1-sol"\n[agents]\ndefault_subagent_model = "gpt-6-luna"\ndefault_subagent_reasoning_effort = "max"\nmax_concurrent_threads_per_session = 20\n[unrelated]\nflag = true\n'''
        (self.home/'config.toml').write_text(self.config,encoding='utf-8')
        self.agents=b'User instructions.\r\n\r\n<!-- codex-control-center:image-generation:start -->\r\nManaged instructions.\r\n<!-- codex-control-center:image-generation:end -->\r\n\r\nTail.\r\n'
        (self.home/'AGENTS.md').write_bytes(self.agents)
        owned={'type':'command','command':'"C:/home/control-center/policy_hook.py"','statusMessage':module.MARKER}
        self.hooks={'hooks':{'SessionEnd':[{'hooks':[owned,{'type':'command','command':'user-command','statusMessage':'User'}]}]}}
        (self.home/'hooks.json').write_bytes(module.json_bytes(self.hooks))
        for name in module.SKILLS:
            folder=self.home/'skills'/name;folder.mkdir(parents=True);(folder/'SKILL.md').write_bytes(b'---\nname: sample\n---\nSkill.\n')
        for name in ['auth.json','history.jsonl','state_5.sqlite']:(self.home/name).write_bytes(b'PRIVATE SENTINEL - never read or modify')
    def tearDown(self):self.temp.cleanup()
    def files(self):return {p.relative_to(self.home):p.read_bytes() for p in self.home.rglob('*') if p.is_file() and 'enhancement-mode' not in p.parts}
    def test_roundtrip_preserves_user_data_and_raw_instructions(self):
        before=self.files();off=self.manager.set_mode('original');self.assertEqual(off['mode'],'original')
        self.assertNotIn(b'Managed instructions.',(self.home/'AGENTS.md').read_bytes())
        self.assertEqual(json.loads((self.home/'hooks.json').read_text(encoding='utf-8'))['hooks']['SessionEnd'][0]['hooks'][0]['command'],'user-command')
        self.assertNotIn('default_subagent_model',tomllib.loads((self.home/'config.toml').read_text(encoding='utf-8'))['agents'])
        for name in module.SKILLS:self.assertFalse((self.home/'skills'/name/'SKILL.md').exists())
        on=self.manager.set_mode('enhanced');self.assertEqual(on['mode'],'enhanced')
        after=self.files()
        for path,data in before.items():
            if path==pathlib.Path('config.toml'):continue
            self.assertEqual(after[path],data,path)
        self.assertTrue(tomllib.loads((self.home/'config.toml').read_text(encoding='utf-8'))['unrelated']['flag'])
        self.assertEqual(tomllib.loads((self.home/'config.toml').read_text(encoding='utf-8'))['agents']['default_subagent_model'],'gpt-6-luna')
    def test_user_edits_while_original_are_merged(self):
        self.manager.set_mode('original')
        with (self.home/'AGENTS.md').open('ab') as f:f.write(b'New user rule.\r\n')
        hooks=json.loads((self.home/'hooks.json').read_text(encoding='utf-8'));hooks['hooks']['UserEvent']=[{'hooks':[{'command':'new-user-hook'}]}];(self.home/'hooks.json').write_bytes(module.json_bytes(hooks))
        self.manager.set_mode('enhanced')
        self.assertIn(b'New user rule.',(self.home/'AGENTS.md').read_bytes());self.assertEqual((self.home/'AGENTS.md').read_bytes().count(b'Managed instructions.'),1)
        self.assertIn('UserEvent',json.loads((self.home/'hooks.json').read_text(encoding='utf-8'))['hooks'])
    def test_invalid_hooks_or_marker_refuses_entire_change(self):
        (self.home/'hooks.json').write_bytes(b'{bad');before=self.files()
        with self.assertRaises(ValueError):self.manager.set_mode('original')
        self.assertEqual(before,self.files())
        (self.home/'hooks.json').write_bytes(module.json_bytes(self.hooks));(self.home/'AGENTS.md').write_text('<!-- codex-control-center:image-generation:start -->',encoding='utf-8');before=self.files()
        with self.assertRaises(RuntimeError):self.manager.set_mode('original')
        self.assertEqual(before,self.files())
    def test_rollback_restores_all_files_on_write_failure(self):
        before=self.files();replace=module.os.replace;counter=0
        def failing(*args):
            nonlocal counter
            counter+=1
            if counter==3:raise OSError('simulated write failure')
            return replace(*args)
        with patch.object(module.os,'replace',side_effect=failing):
            with self.assertRaises(OSError):self.manager.set_mode('original')
        self.assertEqual(before,self.files())
    def test_skill_conflict_refuses_changes(self):
        (self.home/'skills'/module.SKILLS[0]/'SKILL.md.control-center-disabled').write_bytes(b'conflict');before=self.files()
        with self.assertRaises(RuntimeError):self.manager.set_mode('original')
        self.assertEqual(before,self.files())
    def test_repeated_switches_and_native_plugin_flag(self):
        for _ in range(6):self.manager.set_mode('original');self.manager.set_mode('enhanced')
        self.assertEqual((self.home/'AGENTS.md').read_bytes(),self.agents)
        self.assertEqual(json.loads((self.home/'hooks.json').read_text(encoding='utf-8')),self.hooks)
        config=(self.home/'config.toml').read_text(encoding='utf-8');(self.home/'config.toml').write_text(module.set_plugin(config,False))
        self.assertEqual(self.manager.status()['mode'],'original')
        self.manager.set_mode('original');self.assertNotIn(b'Managed instructions.',(self.home/'AGENTS.md').read_bytes())

    def test_unified_catalog_mode_roundtrip_keeps_original_native_and_secrets_unread(self):
        profile={'enabled':True,'provider':'router','catalog':str(self.home/'combined-models.json'),
                 'native_provider':'openai','native_model':'gpt-6.1-sol','native_model_ids':['gpt-6.1-sol']}
        (self.home/'control-center').mkdir(exist_ok=True)
        (self.home/'control-center/unified-routing.json').write_bytes(module.json_bytes(profile))
        self.manager.set_mode('enhanced')
        config=tomllib.loads((self.home/'config.toml').read_text(encoding='utf-8'))
        self.assertEqual(config['model_provider'],'router');self.assertEqual(config['model_catalog_json'],profile['catalog'])
        text=(self.home/'config.toml').read_text(encoding='utf-8').replace('model = "gpt-6.1-sol"','model = "key1/gpt-6.1-sol"')
        (self.home/'config.toml').write_text(text,encoding='utf-8')
        self.manager.set_mode('original')
        config=tomllib.loads((self.home/'config.toml').read_text(encoding='utf-8'))
        self.assertEqual(config['model_provider'],'openai');self.assertNotIn('model_catalog_json',config)
        self.assertEqual(config['model'],'gpt-6.1-sol')
        for name in ['auth.json','history.jsonl','state_5.sqlite']:
            self.assertEqual((self.home/name).read_bytes(),b'PRIVATE SENTINEL - never read or modify')

if __name__=='__main__':unittest.main()
