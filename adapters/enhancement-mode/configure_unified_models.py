"""Install public mixed-provider metadata; never reads keys, auth caches or chat history."""
import argparse,copy,json,pathlib,tomllib,os,urllib.parse,re
from enhancement_mode import ModeManager,apply_routing_mode,json_bytes,read,decode

def local_chatgpt_proxy():
    # Match the user's network configuration only for the official subscription
    # route. Never copy proxy credentials or change third-party destinations.
    candidates=[os.environ.get('HTTPS_PROXY'),os.environ.get('https_proxy')]
    if os.name=='nt':
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Microsoft\Windows\CurrentVersion\Internet Settings') as key:
                if winreg.QueryValueEx(key,'ProxyEnable')[0]:
                    proxy=winreg.QueryValueEx(key,'ProxyServer')[0]
                    if isinstance(proxy,str) and re.fullmatch(r'127\.0\.0\.1:\d{1,5}',proxy):candidates.append('http://'+proxy)
        except OSError:pass
    for candidate in candidates:
        if not candidate:continue
        try:
            value=urllib.parse.urlsplit(candidate)
            if value.scheme in ['http','https'] and value.hostname in ['127.0.0.1','localhost','::1'] and value.port and not value.username and not value.password and value.path in ['','/'] and not value.query and not value.fragment:
                return urllib.parse.urlunsplit((value.scheme,value.netloc,'','',''))
        except ValueError:continue
    return None


def compose(native,legacy,router,home):
    official=[copy.deepcopy(m) for m in native.get('models',[]) if isinstance(m,dict) and isinstance(m.get('slug'),str)]
    if not official:raise ValueError('No official model metadata is available; refresh Codex sign-in first')
    if len({m['slug'] for m in official})!=len(official):raise ValueError('Duplicate official model identifiers')
    updated=copy.deepcopy(router);routes={};models=[];renamed={};seen=set()
    previous_subscription=updated.setdefault('providers',{}).get('subscription',{})
    updated['providers']['subscription']={'name':'官方账号订阅','auth_mode':'chatgpt',
        'base_url':'https://chatgpt.com/backend-api/codex','auth_file':str(pathlib.Path(home)/'auth.json')}
    for key in ['proxy_url','proxy_env']:
        if key in previous_subscription:updated['providers']['subscription'][key]=previous_subscription[key]
    for entry in official:
        slug=entry['slug'];entry['display_name']=entry.get('display_name',slug)+' [账号订阅]'
        previous=next((m for m in legacy.get('models',[]) if m.get('slug')==slug and router.get('routes',{}).get(slug,{}).get('provider')=='subscription'),None)
        if previous and isinstance(previous.get('display_name'),str):entry['display_name']=previous['display_name']
        entry['provider']='subscription';models.append(entry);seen.add(slug)
        routes[slug]={'provider':'subscription','model':slug}
    for source in legacy.get('models',[]):
        old=source.get('slug');route=router.get('routes',{}).get(old)
        if not isinstance(route,dict) or route.get('provider') in ['subscription','plus','b']:continue
        entry=copy.deepcopy(source);provider=route['provider']
        slug=('key1/'+old.removeprefix('key1/')) if provider=='a' else old
        if slug in seen:raise ValueError('Mixed-model identifier collision: '+slug)
        renamed[old]=slug;entry['slug']=slug;entry['provider']=provider
        if provider=='a' and not old.startswith('key1/'):entry['display_name']=entry.get('display_name',old).replace(' [Key 1]','')+' [Key 1]'
        elif provider=='minimax' and not entry.get('display_name'):entry['display_name']='MiniMax M3.1 Flash [MiniMax 官方]'
        entry['priority']=len(models);models.append(entry);seen.add(slug);routes[slug]=copy.deepcopy(route)
    updated['routes']=routes
    return {'models':models},updated,renamed


def configure(home,workspace,apply=False):
    home=pathlib.Path(home).resolve();workspace=pathlib.Path(workspace).resolve()
    manager=ModeManager(home,workspace)
    with manager.lock():
        config_path=home/'config.toml';config=decode(read(config_path));settings=tomllib.loads(config)
        profile_path=home/'control-center/unified-routing.json'
        profile=json.loads(decode(read(profile_path)) or '{}')
        catalog_path=pathlib.Path(profile.get('catalog') or home/'model-router-sandbox/combined-models.json').resolve()
        router_path=catalog_path.with_name('router.json')
        catalog=json.loads(decode(read(catalog_path)));router=json.loads(decode(read(router_path)))
        native=json.loads(decode(read(home/'models_cache.json')))
        combined,routes,renamed=compose(native,catalog,router,home)
        proxy=local_chatgpt_proxy()
        if proxy and not any(k in routes['providers']['subscription'] for k in ['proxy_url','proxy_env']):routes['providers']['subscription']['proxy_url']=proxy
        profile.update(version=1,enabled=True,provider='router',catalog=str(catalog_path),router=str(router_path),
            native_provider=profile.get('native_provider') or (settings.get('model_provider') if settings.get('model_provider') in ['openai','openai_http'] else 'openai'),
            native_catalog=profile.get('native_catalog') if profile else settings.get('model_catalog_json'),
            native_model=profile.get('native_model') or settings.get('model') or native['models'][0]['slug'],native_model_ids=[m['slug'] for m in native['models']])
        if settings.get('model_providers',{}).get('router',{}).get('requires_openai_auth') is not True:
            # Keep sign-in and native OAuth renewal active in enhanced mode. The
            # gateway replaces authorization per destination and never forwards a
            # ChatGPT bearer token to a third-party provider.
            from enhancement_mode import section
            loc=section(config,'model_providers.router')
            if loc is None:
                config+='\n[model_providers.router]\nname = "Codex Control Center"\nbase_url = "http://127.0.0.1:18748/v1"\nwire_api = "responses"\nrequires_openai_auth = true\nenv_http_headers = { "X-Local-Router-Key" = "CODEX_CONTROL_CENTER_ROUTER_TOKEN" }\n'
                loc=section(config,'model_providers.router')
            start,end=loc;part=config[start:end]
            part=re.sub(r'(?m)^requires_openai_auth\s*=.*$', 'requires_openai_auth = true',part)
            if 'requires_openai_auth' not in part:part+='requires_openai_auth = true\n'
            config=config[:start]+part+config[end:]
        mode='enhanced' if manager.flag() else 'original'
        config=apply_routing_mode(config,mode,profile)
        changes={profile_path:json_bytes(profile),catalog_path:json_bytes(combined),router_path:json_bytes(routes),config_path:config.encode('utf-8')}
        policy_path=home/'control-center/subagent-policy.json'
        if policy_path.exists():
            policy=json.loads(decode(read(policy_path)))
            for key in ['primary','overflow']:
                slot=policy.get(key,{})
                if slot.get('provider')=='a' and slot.get('model') in renamed:slot['model']=renamed[slot['model']]
            changes[policy_path]=json_bytes(policy)
            agents_path=home/'AGENTS.md';agents=decode(read(agents_path))
            from enhancement_mode import BLOCK
            def rename_block(match):
                value=match[0]
                if match[1]=='subagent-policy':
                    for old,new in renamed.items():
                        if old!=new:value=value.replace('`a` / `'+old+'`','`a` / `'+new+'`')
                return value
            changes[agents_path]=BLOCK.sub(rename_block,agents).encode('utf-8')
        pending_path=home/'control-center/native-session-migration/state.json'
        if pending_path.exists():
            pending=json.loads(decode(read(pending_path)));pending['reloadRequired']=True;changes[pending_path]=json_bytes(pending)
        changes={path:value for path,value in changes.items() if read(path)!=value}
        result={'success':True,'mode':mode,'officialModels':len(native['models']),'totalModels':len(combined['models']),
            'providers':list(routes['providers']),'key1AliasesNamespaced':True,'nativeOAuthRenewalEnabled':True,
            'reloadRequired':True,'inferenceRequests':0,'changedFiles':len(changes)}
        if apply:
            result['backup']=str(manager.commit(changes,mode)) if changes else None
        else:result['dryRun']=True
        return result


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--codex-home',required=True);p.add_argument('--workspace',required=True);p.add_argument('--apply',action='store_true');args=p.parse_args()
    try:print(json.dumps(configure(args.codex_home,args.workspace,args.apply),ensure_ascii=False))
    except Exception as error:print(json.dumps({'success':False,'error':type(error).__name__,'detail':str(error)},ensure_ascii=False));raise SystemExit(1)
