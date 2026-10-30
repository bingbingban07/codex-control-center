"""Initialize a fresh installation without importing machine/user state."""
from __future__ import annotations
import argparse,json,os,pathlib,secrets,shutil,subprocess,sys,tomllib

def initialize(home:pathlib.Path,state:pathlib.Path,app:pathlib.Path,node:str|None):
    home=home.resolve();state=state.resolve();app=app.resolve();written=[]
    def create(path,value):
        if path.exists():return
        path.parent.mkdir(parents=True,exist_ok=True)
        if isinstance(value,dict):value=json.dumps(value,ensure_ascii=False,indent=2)+'\n'
        # Never overwrite a file another installer/user created in the meantime.
        with path.open('x',encoding='utf-8',newline='\n') as f:f.write(value)
        written.append(str(path))
    create(home/'config.toml','# Codex Control Center: native sign-in; Codex chooses its own model.\n')
    # Validate before creating any routing metadata; never read auth or chats.
    config=tomllib.loads((home/'config.toml').read_text(encoding='utf-8-sig'))
    router=home/'model-router-sandbox'
    create(router/'router.json',{'host':'127.0.0.1','port':18748,'auth_token_file':str(router/'router.token'),'providers':{},'routes':{}})
    create(router/'combined-models.json',{'models':[]})
    create(router/'router.token',secrets.token_urlsafe(48)+'\n')
    if node:create(router/'runtime.json',{'node':str(pathlib.Path(node).resolve())})
    engine=app/'adapters/router-observability/router-engine.mjs'
    for name in ['router-engine.mjs']:
        destination=router/'router-control-center-engine.mjs'
        if not destination.exists() and engine.is_file():shutil.copyfile(engine,destination);written.append(str(destination))
    create(router/'router.mjs',"// CODEX_CONTROL_CENTER_HOT_RELOAD_V1\nimport {main} from './control-center-runtime/hot-router.mjs';\n"+
        "import {dirname,join} from 'node:path';import {fileURLToPath} from 'node:url';\n"+
        "main([...process.argv.slice(2),'--engine',join(dirname(fileURLToPath(import.meta.url)),'router-control-center-engine.mjs'),'--codex-home',"+json.dumps(str(home))+ "]).catch(()=>{process.stderr.write('Router startup failed.\\n');process.exitCode=1;});\n")
    create(home/'control-center/subagent-policy.json',{'version':1,'enabled':False,'auto_trust_hooks':False,'mode':'failure_fallback','revision':'','router_config_path':str(router/'router.json'),'primary':{'provider':'','model':'','effort':''},'overflow':{'provider':'','model':'','effort':''}})
    create(home/'control-center/image-profiles.json',{'version':1,'active_provider':'official','profiles':{}})
    create(home/'control-center/minimax-media.json',{'version':1,'base_url':'https://api.minimax.cn','credential_file':str(home/'secrets/control-center-minimax.dpapi'),'speech_model':'','image_model':'','video_model':'','music_model':'','music_enabled':False})
    create(state/'preferences.json',{'codexHome':str(home),'theme':'system','density':'large','autoTrustManagedHooks':False})
    create(state/'wallpaper/wallpaper.json',{'enabled':False,'imageId':None,'fit':'cover','positionX':50,'positionY':50,'opacity':1,'darkness':.2,'blur':0,'overlay':.35,'zoom':1})
    create(state/'installation.json',{'application_root':str(app),'codex_home':str(home)})
    helper=app/'adapters/image-provider/i2m_image_api.py'
    installed=home/'skills/i2m-imagegen/scripts/image_api.py'
    if helper.is_file() and not installed.exists():
        installed.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(helper,installed);written.append(str(installed))
    # No user instructions, hooks, login, sessions or provider credentials copied.
    return {'success':True,'createdFiles':written,'existingConfigPreserved':bool(config),'defaultModel':'Codex default','routingEnabled':False,'subagentPolicyEnabled':False,'mediaEnabled':False}

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--codex-home',required=True);p.add_argument('--state-home',required=True);p.add_argument('--app-root',required=True);p.add_argument('--node');a=p.parse_args()
    print(json.dumps(initialize(pathlib.Path(a.codex_home),pathlib.Path(a.state_home),pathlib.Path(a.app_root),a.node),ensure_ascii=False))
