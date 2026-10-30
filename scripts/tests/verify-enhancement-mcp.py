"""Handshake with the installed local plugin; inspect status only, never generate content."""
import argparse,json,pathlib,subprocess
p=argparse.ArgumentParser();p.add_argument('--plugin',required=True);p.add_argument('--out',required=True);a=p.parse_args()
root=pathlib.Path(a.plugin);manifest=json.loads((root/'.mcp.json').read_text(encoding='utf-8-sig'))
server=manifest['mcpServers']['enhancements']
requests=[{'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'local-verification','version':'1'}}},
 {'jsonrpc':'2.0','method':'notifications/initialized'},
 {'jsonrpc':'2.0','id':2,'method':'tools/list','params':{}},
 {'jsonrpc':'2.0','id':3,'method':'tools/call','params':{'name':'enhancement_status','arguments':{}}}]
proc=subprocess.run([server['command'],*server['args']],input=''.join(json.dumps(r)+'\n' for r in requests),capture_output=True,text=True,encoding='utf-8',timeout=15,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
assert proc.returncode==0,'Plugin server failed'
responses=[json.loads(line) for line in proc.stdout.splitlines() if line.strip()]
assert len(responses)==3 and all('result' in r for r in responses),responses
tools=responses[1]['result']['tools'];assert {t['name'] for t in tools}=={'enhancement_status','set_enhancement_mode'}
status=json.loads(responses[2]['result']['content'][0]['text']);assert status['success'] and status['sharedLoginAndChats']
result={'success':True,'nativePluginMcpHandshake':True,'toolNames':[t['name'] for t in tools],'mode':status['mode'],'mutationRequests':0,'inferenceRequests':0,'credentialsRead':False}
path=pathlib.Path(a.out);path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))
