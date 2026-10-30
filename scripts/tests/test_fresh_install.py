import importlib.util,json,pathlib,tempfile,tomllib,unittest

ROOT=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('initialize_defaults',ROOT/'scripts/initialize-defaults.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class FreshInstall(unittest.TestCase):
    def test_fresh_defaults_are_empty_and_native(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);home=root/'home';state=root/'state'
            result=module.initialize(home,state,ROOT,None)
            self.assertTrue(result['success']);self.assertEqual(tomllib.loads((home/'config.toml').read_text()),{})
            self.assertEqual(json.loads((home/'model-router-sandbox/router.json').read_text())['providers'],{})
            self.assertEqual(json.loads((home/'model-router-sandbox/combined-models.json').read_text())['models'],[])
            policy=json.loads((home/'control-center/subagent-policy.json').read_text());self.assertFalse(policy['enabled']);self.assertEqual(policy['primary']['model'],'')
            self.assertFalse(json.loads((state/'wallpaper/wallpaper.json').read_text())['enabled'])
            for name in ['auth.json','AGENTS.md','hooks.json','sessions','secrets']:
                self.assertFalse((home/name).exists())
            self.assertTrue((home/'model-router-sandbox/router-control-center-engine.mjs').is_file())
    def test_reinstall_does_not_replace_existing_data_or_token(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory);home=root/'home';state=root/'state';home.mkdir()
            config=b'# existing\r\nmodel = "local-choice"\r\n';(home/'config.toml').write_bytes(config)
            module.initialize(home,state,ROOT,None);token=(home/'model-router-sandbox/router.token').read_bytes()
            policy=home/'control-center/subagent-policy.json';policy.write_text('{"enabled":true,"user":"kept"}')
            module.initialize(home,state,ROOT,None)
            self.assertEqual((home/'config.toml').read_bytes(),config);self.assertEqual((home/'model-router-sandbox/router.token').read_bytes(),token)
            self.assertEqual(json.loads(policy.read_text())['user'],'kept')
    def test_tokens_are_new_for_each_installation(self):
        with tempfile.TemporaryDirectory() as directory:
            root=pathlib.Path(directory)
            for name in ['one','two']:module.initialize(root/name,root/(name+'-state'),ROOT,None)
            self.assertNotEqual((root/'one/model-router-sandbox/router.token').read_bytes(),(root/'two/model-router-sandbox/router.token').read_bytes())

if __name__=='__main__':unittest.main()
