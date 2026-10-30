import assert from 'node:assert/strict';
import test from 'node:test';
import http from 'node:http';
import { once } from 'node:events';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

test('subscription canonical names, Key 1 aliases and MiniMax use independent credentials', async t => {
  const enginePath=join(process.env.USERPROFILE,'.codex','model-router-sandbox','router-control-center-engine.mjs');
  const { createRouter }=await import(pathToFileURL(enginePath).href);
  const root=await mkdtemp(join(tmpdir(),'codex-mixed-subscription-'));
  t.after(()=>rm(root,{recursive:true,force:true}));
  const localToken='synthetic-local-token-with-no-account-access';
  const tokenPath=join(root,'router.token'),authPath=join(root,'synthetic-auth.json');
  await writeFile(tokenPath,localToken);
  await writeFile(authPath,JSON.stringify({tokens:{access_token:'synthetic-subscription-token-one',account_id:'synthetic-account'}}));
  const requests=[];
  const upstream=http.createServer(async(req,res)=>{
    let raw='';for await(const chunk of req)raw+=chunk;
    requests.push({body:JSON.parse(raw),headers:req.headers});
    res.writeHead(200,{'content-type':'application/json'});res.end(JSON.stringify({status:'completed',output:[]}));
  });
  upstream.listen(0,'127.0.0.1');await once(upstream,'listening');
  t.after(()=>new Promise(resolve=>upstream.close(resolve)));
  const destinations=[];
  const server=await createRouter({host:'127.0.0.1',port:0,auth_token_file:tokenPath,
    providers:{subscription:{auth_mode:'chatgpt',base_url:'https://chatgpt.com/backend-api/codex',auth_file:authPath},
      a:{base_url:'https://key1.example.invalid/v1',key_env:'SYNTHETIC_A'},
      minimax:{base_url:'https://minimax.example.invalid/v1',key_env:'SYNTHETIC_MINIMAX'}},
    routes:{'gpt-6.1-sol':{provider:'subscription',model:'gpt-6.1-sol'},
      'key1/gpt-6.1-sol':{provider:'a',model:'gpt-6.1-sol'},
      'MiniMax-M3.1-Flash':{provider:'minimax',model:'MiniMax-M3.1-Flash'}}},
    {env:{SYNTHETIC_A:'synthetic-key-a',SYNTHETIC_MINIMAX:'synthetic-key-minimax'},logger:()=>{},
      requestTransport:(url,options,callback)=>{destinations.push(url.href);return http.request(`http://127.0.0.1:${upstream.address().port}/responses`,options,callback);}});
  server.listen(0,'127.0.0.1');await once(server,'listening');t.after(()=>new Promise(resolve=>server.close(resolve)));
  const call=async(model,withHeader=true)=>fetch(`http://127.0.0.1:${server.address().port}/v1/responses`,{
    method:'POST',headers:{'content-type':'application/json','authorization':'Bearer '+localToken,
      ...(withHeader?{'x-local-router-key':localToken}:{}),'chatgpt-account-id':'incoming-must-not-leak'},
    body:JSON.stringify({model,input:[]})});
  for(const name of ['gpt-6.1-sol','key1/gpt-6.1-sol','MiniMax-M3.1-Flash']){
    const result=await call(name);assert.equal(result.status,200);await result.text();
  }
  assert.deepEqual(destinations,['https://chatgpt.com/backend-api/codex/responses','https://key1.example.invalid/v1/responses','https://minimax.example.invalid/v1/responses']);
  assert.equal(requests[0].headers.authorization,'Bearer synthetic-subscription-token-one');
  assert.equal(requests[1].headers.authorization,'Bearer synthetic-key-a');
  assert.equal(requests[2].headers.authorization,'Bearer synthetic-key-minimax');
  assert.equal(requests[0].headers['chatgpt-account-id'],'synthetic-account');
  assert.equal(requests[1].headers['chatgpt-account-id'],undefined);
  assert.equal(requests[2].headers['chatgpt-account-id'],undefined);
  assert.equal(requests[1].body.model,'gpt-6.1-sol');
  await writeFile(authPath,JSON.stringify({tokens:{access_token:'synthetic-subscription-token-refreshed',account_id:'synthetic-account'}}));
  const refreshed=await call('gpt-6.1-sol');assert.equal(refreshed.status,200);await refreshed.text();
  assert.equal(requests[3].headers.authorization,'Bearer synthetic-subscription-token-refreshed');
  const rejected=await call('gpt-6.1-sol',false);assert.equal(rejected.status,401);await rejected.text();
  assert.equal(destinations.length,4,'Missing local authorization contacted an upstream');
});
