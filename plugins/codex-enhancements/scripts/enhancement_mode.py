"""Local, reversible enhancement modes. Never reads auth, sessions, history or key files."""
from __future__ import annotations
import argparse, base64, contextlib, copy, hashlib, json, os, pathlib, re, subprocess, sys, time, tomllib, uuid

PLUGIN='codex-enhancements@codex-control-center'
MARKER='Codex 管理台 · 子代理全局分流'
BLOCK=re.compile(r'<!-- codex-control-center:([a-z-]+):start -->.*?<!-- codex-control-center:\1:end -->',re.S)
KEYS=['default_subagent_model','default_subagent_reasoning_effort','max_concurrent_threads_per_session']
SKILLS=['i2m-imagegen','minimax-media']
TABLE=re.compile(r'(?m)^\[([^\n]+)\]\s*(?:\r?\n|$)')
def digest(data):return hashlib.sha256(data).hexdigest()
def read(path):return path.read_bytes() if path.exists() else None
def decode(data):return (data or b'').decode('utf-8-sig')
def encode(data):return base64.b64encode(data).decode() if data is not None else None
def uncode(data):return base64.b64decode(data) if data is not None else None
def json_bytes(value):return (json.dumps(value,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def section(text,name):
    matches=list(TABLE.finditer(text))
    for i,m in enumerate(matches):
        if m[1].strip()==name:return m.end(),matches[i+1].start() if i+1<len(matches) else len(text)
    return None
def set_plugin(text,enabled):
    name='plugins.'+json.dumps(PLUGIN);loc=section(text,name)
    if loc is None:text+='\n['+name+']\nenabled = '+str(enabled).lower()+'\n'
    else:
        start,end=loc;part=text[start:end];line='enabled = '+str(enabled).lower()
        part=re.sub(r'(?m)^enabled\s*=.*$',line,part,count=1) if re.search(r'(?m)^enabled\s*=',part) else line+'\n'+part
        text=text[:start]+part+text[end:]
    tomllib.loads(text);return text

def set_root_values(text,values):
    first=TABLE.search(text);end=first.start() if first else len(text)
    root,tail=text[:end],text[end:]
    for key,value in values.items():
        expression=re.compile(r'(?m)^'+re.escape(key)+r'\s*=.*(?:\r?\n|$)')
        replacement='' if value is None else key+' = '+json.dumps(value,ensure_ascii=False)+'\n'
        if expression.search(root):root=expression.sub(lambda _:replacement,root,count=1)
        elif replacement:root+=('' if not root or root.endswith('\n') else '\n')+replacement
    result=root+tail;tomllib.loads(result);return result

def apply_routing_mode(config,mode,profile):
    if not profile.get('enabled'):return config
    if mode=='enhanced':
        return set_root_values(config,{'model_provider':profile['provider'],'model_catalog_json':profile['catalog']})
    values={'model_provider':profile.get('native_provider','openai'),'model_catalog_json':profile.get('native_catalog')}
    if tomllib.loads(config).get('model') not in profile.get('native_model_ids',[]):values['model']=profile['native_model']
    return set_root_values(config,values)

class ModeManager:
    def __init__(self,home,workspace,state_home=None):
        self.home=pathlib.Path(home).absolute();self.workspace=pathlib.Path(workspace).absolute()
        self.state_home=pathlib.Path(state_home).absolute() if state_home else pathlib.Path(os.environ.get('LOCALAPPDATA',str(self.home)))/'CodexControlCenter'
        self.root=self.home/'control-center'/'enhancement-mode';self.path=self.root/'state.json'
    def flag(self):
        config=tomllib.loads(decode(read(self.home/'config.toml')))
        return config.get('plugins',{}).get(PLUGIN,{}).get('enabled',False)
    def status(self):
        state=json.loads(decode(read(self.path)) or '{}');enabled=self.flag()
        return {'success':True,'mode':'enhanced' if enabled else 'original','savedMode':state.get('mode','uninitialized'),
            'initialized':bool(state.get('version')),'pluginEnabled':enabled,'sharedLoginAndChats':True,
            'reloadRequired':bool(state.get('reloadRequired')),'instructionsAndHooksReload':'新聊天或重开 Codex 后更新',
            'wallpaperRuntime':'原版模式将恢复原生外观；增强模式需通过桌面 Codex 重开以连接',
            'backup':state.get('lastBackup')}
    @contextlib.contextmanager
    def lock(self):
        self.root.mkdir(parents=True,exist_ok=True)
        with (self.root/'mode.lock').open('a+b') as lock:
            if os.name=='nt':
                import msvcrt
                lock.seek(0);lock.write(b'0');lock.flush();lock.seek(0)
                try:msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
                except OSError:raise RuntimeError('另一个模式切换正在进行，请稍后重试。')
            try:yield
            finally:
                if os.name=='nt':lock.seek(0);msvcrt.locking(lock.fileno(),msvcrt.LK_UNLCK,1)
    def commit(self,changes,mode):
        # Stage, fingerprint and back up every affected file before publishing any write.
        backup=self.root/'backups'/str(uuid.uuid4());backup.mkdir(parents=True)
        if self.path in changes:
            state=json.loads(decode(changes[self.path]));state['lastBackup']=str(backup);changes[self.path]=json_bytes(state)
        originals={p:read(p) for p in changes};stages={};written=[]
        manifest=[]
        for index,(path,data) in enumerate(originals.items()):
            if data is not None:(backup/str(index)).write_bytes(data)
            manifest.append({'path':str(path),'backup':str(index) if data is not None else None,'sha256':digest(data) if data is not None else None})
        (backup/'manifest.json').write_bytes(json_bytes({'mode':mode,'files':manifest}))
        try:
            for path,data in changes.items():
                if data is not None:
                    path.parent.mkdir(parents=True,exist_ok=True);stage=path.with_name(path.name+'.'+uuid.uuid4().hex+'.tmp');stage.write_bytes(data);stages[path]=stage
            for path in changes:
                if read(path)!=originals[path]:raise RuntimeError('文件被外部修改，模式切换已取消：'+str(path))
            for path,data in changes.items():
                if read(path)!=originals[path]:raise RuntimeError('提交期间文件发生变化：'+str(path))
                if data is None:
                    if path.exists():path.unlink()
                else:os.replace(stages[path],path)
                written.append(path)
        except:
            for path in reversed(written):
                if read(path)!=changes[path]:raise RuntimeError('回滚发现外部修改，请保留备份：'+str(backup))
                original=originals[path]
                if original is None:path.unlink(missing_ok=True)
                else:
                    rollback=path.with_name(path.name+'.rollback.tmp');rollback.write_bytes(original);os.replace(rollback,path)
            raise
        finally:
            for stage in stages.values():stage.unlink(missing_ok=True)
        return backup
    def set_mode(self,mode):
        if mode not in ['original','enhanced']:raise ValueError('Unknown mode')
        with self.lock():
            state=json.loads(decode(read(self.path)) or '{}');state.setdefault('version',1);changes={}
            restore=state.setdefault('restore',{})
            config_path=self.home/'config.toml';config=decode(read(config_path));tomllib.loads(config)
            # Remove only managed blocks, preserving all user-authored instructions and line endings.
            agents_path=self.home/'AGENTS.md';agents=decode(read(agents_path))
            starts=len(re.findall(r'<!-- codex-control-center:[a-z-]+:start -->',agents))
            matches=list(BLOCK.finditer(agents))
            ends=len(re.findall(r'<!-- codex-control-center:[a-z-]+:end -->',agents))
            if starts!=len(matches) or ends!=len(matches):raise RuntimeError('受管理指令边界不完整，已拒绝切换。')
            if mode=='original':
                if matches:
                    restore['agentsFull']=encode(read(agents_path));restore['blocks']=[m[0] for m in matches]
                    agents=BLOCK.sub('',agents);restore['agentsStrippedHash']=digest(agents.encode('utf-8'));changes[agents_path]=agents.encode('utf-8')
            elif restore.get('blocks'):
                if digest(agents.encode('utf-8'))==restore.get('agentsStrippedHash'):changes[agents_path]=uncode(restore['agentsFull'])
                else:
                    existing={m[1] for m in matches};missing=[b for b in restore['blocks'] if BLOCK.fullmatch(b)[1] not in existing]
                    if missing:changes[agents_path]=(agents+('\n' if agents and not agents.endswith('\n') else '')+'\n'.join(missing)+'\n').encode('utf-8')
            hooks_path=self.home/'hooks.json';hooks_raw=read(hooks_path);hooks=json.loads(decode(hooks_raw) or '{"hooks":{}}')
            if not isinstance(hooks.get('hooks',{}),dict):raise RuntimeError('hooks.json 格式无效，已拒绝切换。')
            managed={};stripped=copy.deepcopy(hooks)
            for event,groups in hooks.get('hooks',{}).items():
                kept=[]
                for group in groups:
                    owned=[h for h in group.get('hooks',[]) if h.get('statusMessage')==MARKER and 'control-center' in h.get('command','') and 'policy_hook.py' in h.get('command','')]
                    other=[h for h in group.get('hooks',[]) if h not in owned]
                    if owned:managed.setdefault(event,[]).append({**group,'hooks':owned})
                    if other:kept.append({**group,'hooks':other})
                stripped.setdefault('hooks',{})[event]=kept
            if mode=='original' and managed:
                restore['hooksFull']=encode(hooks_raw);restore['hookGroups']=managed
                value=json_bytes(stripped);restore['hooksStrippedHash']=digest(value);changes[hooks_path]=value
            elif mode=='enhanced' and restore.get('hookGroups'):
                if digest(hooks_raw or b'')==restore.get('hooksStrippedHash'):changes[hooks_path]=uncode(restore['hooksFull'])
                else:
                    for event,groups in restore['hookGroups'].items():
                        target=hooks.setdefault('hooks',{}).setdefault(event,[])
                        for group in groups:
                            if group not in target:target.append(group)
                    changes[hooks_path]=json_bytes(hooks)
            loc=section(config,'agents')
            if loc:
                start,end=loc;part=config[start:end]
                if mode=='original':
                    owned={key:re.search(r'(?m)^'+key+r'\s*=.*(?:\r?\n|$)',part) for key in KEYS}
                    lines={key:m[0] for key,m in owned.items() if m}
                    if lines:restore['agentLines']=lines
                    for key in lines:part=re.sub(r'(?m)^'+key+r'\s*=.*(?:\r?\n|$)','',part)
                else:
                    for key,line in restore.get('agentLines',{}).items():
                        # Retired subscription aliases must not return after a mode round trip.
                        if key=='default_subagent_model' and tomllib.loads(config).get('model_provider') in ['openai','openai_http']:
                            line=re.sub(r'"plus/(gpt-[^"]+)"',r'"\1"',line)
                        if not re.search(r'(?m)^'+key+r'\s*=',part):part+=line
                config=config[:start]+part+config[end:]
            elif mode=='enhanced' and restore.get('agentLines'):config+='\n[agents]\n'+''.join(restore['agentLines'].values())
            # Disable discovery, keeping skill helpers, data and encrypted references at their old paths.
            skill_files=[self.home/'skills'/name/'SKILL.md' for name in SKILLS]
            # mmx was installed as a separate skill; only remove our safety-routing paragraph,
            # preserving its original CLI workflow and every unrelated skill.
            for mmx in [self.home/'skills/mmx-cli/SKILL.md',self.home.parent/'.agents/skills/mmx-cli/SKILL.md']:
                mmx_text=decode(read(mmx));start='<!-- codex-control-center:minimax-media-route -->';end='<!-- codex-control-center:minimax-media-route:end -->'
                key=str(mmx)
                if mode=='original' and start in mmx_text:
                    if mmx_text.count(start)!=1 or mmx_text.count(end)!=1:raise RuntimeError('mmx 指令边界存在冲突。')
                    begin=mmx_text.index(start);finish=mmx_text.index(end)+len(end);content=mmx_text[begin:finish]
                    stripped_mmx=(mmx_text[:begin]+mmx_text[finish:]).encode('utf-8')
                    restore.setdefault('mmxBlocks',{})[key]={'block':content,'full':encode(read(mmx)),'strippedHash':digest(stripped_mmx)};changes[mmx]=stripped_mmx
                elif mode=='enhanced' and key in restore.get('mmxBlocks',{}) and start not in mmx_text:
                    # Reinsert after YAML frontmatter so the skill remains discoverable.
                    saved_mmx=restore['mmxBlocks'][key]
                    offset=mmx_text.find('\n---',3)+4 if mmx_text.startswith('---') else 0
                    changes[mmx]=uncode(saved_mmx['full']) if digest(read(mmx) or b'')==saved_mmx['strippedHash'] else (mmx_text[:offset]+'\n\n'+saved_mmx['block']+mmx_text[offset:]).encode('utf-8')
            for active in skill_files:
                name=active.parent.name;parked=active.with_name('SKILL.md.control-center-disabled')
                if mode=='original' and active.exists():
                    if parked.exists() and read(parked)!=read(active):raise RuntimeError('技能停用副本存在冲突：'+name)
                    changes[parked]=read(active);changes[active]=None
                elif mode=='enhanced' and parked.exists():
                    if active.exists() and read(active)!=read(parked):raise RuntimeError('技能恢复存在冲突：'+name)
                    changes[active]=read(parked);changes[parked]=None
            routing_path=self.home/'control-center'/'unified-routing.json'
            routing=json.loads(decode(read(routing_path)) or '{}')
            before_provider=tomllib.loads(config).get('model_provider')
            config=apply_routing_mode(config,mode,routing)
            # Schedule the user-authorized old-chat binding sync for the next cold
            # desktop launch. The mode transaction never opens chat history.
            pending_path=self.home/'control-center/native-session-migration/state.json'
            if routing.get('enabled') and before_provider!=tomllib.loads(config).get('model_provider') and pending_path.exists():
                pending=json.loads(decode(read(pending_path)));pending['reloadRequired']=True
                changes[pending_path]=json_bytes(pending)
            config=set_plugin(config,mode=='enhanced');changes[config_path]=config.encode('utf-8')
            state.update(mode=mode,reloadRequired=True,sharedLoginAndChats=True)
            # Record anticipated backup path only after a successful transaction; state itself is transactional.
            changes[self.path]=json_bytes(state)
            changes={path:value for path,value in changes.items() if read(path)!=value}
            if changes:backup=self.commit(changes,mode)
            else:backup=None
            result=self.status();result['backup']=str(backup) if backup else None;result['changedFiles']=len(changes)
            return result

def default_home():return pathlib.Path(os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex')))
def main():
    parser=argparse.ArgumentParser();parser.add_argument('action',choices=['status','original','enhanced','reconcile','hook','serve']);parser.add_argument('--codex-home',default=str(default_home()));parser.add_argument('--workspace',default=str(pathlib.Path(__file__).resolve().parents[2]));parser.add_argument('--state-home')
    args=parser.parse_args();manager=ModeManager(args.codex_home,args.workspace,args.state_home)
    if args.action=='serve':serve(manager);return
    if args.action in ['reconcile','hook']:result=manager.set_mode('enhanced' if manager.flag() else 'original')
    elif args.action=='status':result=manager.status()
    else:result=manager.set_mode(args.action)
    print(json.dumps({} if args.action=='hook' else result,ensure_ascii=True))

def serve(manager):
    tools=[{'name':'enhancement_status','description':'查看 Codex 原版/增强模式和共享登录、聊天状态。','inputSchema':{'type':'object','properties':{},'additionalProperties':False}},
        {'name':'set_enhancement_mode','description':'按用户明确要求启用增强模式或恢复原版模式。保留登录、聊天和用户指令；需要重开 Codex 使全部功能更新。','inputSchema':{'type':'object','properties':{'mode':{'type':'string','enum':['original','enhanced']}},'required':['mode'],'additionalProperties':False}}]
    for line in sys.stdin:
        request=None
        try:
            request=json.loads(line);method=request.get('method');params=request.get('params',{});rid=request.get('id')
            if rid is None:continue
            if method=='initialize':result={'protocolVersion':params.get('protocolVersion','2024-11-05'),'capabilities':{'tools':{}},'serverInfo':{'name':'codex-enhancements','version':'0.9.0'}}
            elif method=='ping':result={}
            elif method=='tools/list':result={'tools':tools}
            elif method=='tools/call':
                if params.get('name')=='enhancement_status':value=manager.status()
                elif params.get('name')=='set_enhancement_mode':value=manager.set_mode(params.get('arguments',{}).get('mode'))
                else:raise ValueError('Unknown tool')
                result={'content':[{'type':'text','text':json.dumps(value,ensure_ascii=True)}]}
            else:raise ValueError('Unsupported method')
            response={'jsonrpc':'2.0','id':rid,'result':result}
        except Exception as error:
            response={'jsonrpc':'2.0','id':request.get('id') if isinstance(request,dict) else None,'error':{'code':-32603,'message':str(error)}}
        print(json.dumps(response,ensure_ascii=True),flush=True)

if __name__=='__main__':
    try:main()
    except Exception as error:print(json.dumps({'success':False,'error':str(error)},ensure_ascii=True));raise SystemExit(1)
