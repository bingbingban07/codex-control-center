"""Actual nested leaf colors including native layered important utilities."""
import argparse,base64,io,json,pathlib,re
from PIL import Image,ImageChops
from playwright.sync_api import sync_playwright
p=argparse.ArgumentParser();p.add_argument('--script',required=True);p.add_argument('--disable',required=True);p.add_argument('--out',required=True);p.add_argument('--wallpaper-root');args=p.parse_args()
out=pathlib.Path(args.out).resolve();out.mkdir(parents=True,exist_ok=True)
def load(path):
    s=pathlib.Path(path).read_text(encoding='utf-8-sig');g="location.protocol!=='app:'||location.hostname!=='-'||location.pathname!=='/index.html'";assert s.count(g)==1
    return s.replace(g,"location.protocol!=='about:'")
apply,disable=map(load,[args.script,args.disable]);m=re.search(r'const p=(\{[^\n]+\}), key=',apply);payload=json.loads(m[1]);payload['overlay']=0
if args.wallpaper_root:
    library=pathlib.Path(args.wallpaper_root).resolve();settings=json.loads((library/'wallpaper.json').read_text());asset=library/'library'/settings['imageId'];meta=json.loads((asset/'asset.json').read_text());lower={k.lower():v for k,v in meta.items()};file=(asset/lower['filename']).resolve();assert file.is_relative_to(asset.resolve())
    payload.update(image='data:'+lower['mediatype']+';base64,'+base64.b64encode(file.read_bytes()).decode(),imageKey=None,darkness=settings.get('darkness',1-settings.get('opacity',.8)),x=settings['positionX'],y=settings['positionY'],zoom=settings['zoom'],fit=settings['fit'],blur=settings['blur'])
apply=apply[:m.start(1)]+json.dumps(payload,separators=(',',':'))+apply[m.end(1):]
icon='<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M4 20l2-6L16 4l4 4L10 18zM14 6l4 4"/></svg>'
fixture='''<style>
*{box-sizing:border-box}body{margin:0;font:20px/1.5 "Segoe UI","Microsoft YaHei UI",sans-serif;color:#111}
@layer theme,base,components,utilities;
#root{height:100vh;background:var(--color-surface);--color-surface:rgb(255,255,255)}
html[data-theme=dark] body{color:#f8f8f8}html[data-theme=dark] #root{--color-surface:rgb(24,24,24)}
main{height:100vh;background:var(--color-surface);padding:38px 36px}
.backdrop{position:absolute;inset:0;z-index:0;background:linear-gradient(110deg,#c4b8c5 0 35%,#423754 35% 65%,#8c779f 65%);}
.row{position:relative;z-index:1;display:flex;gap:8px;align-items:center;margin:0 0 24px;height:32px;max-width:720px;background:transparent}
.text-text-60{color:rgb(0 0 0 / .6)}.summary{--agent-activity-summary-color:rgb(0 0 0 / .6);color:var(--agent-activity-summary-color)}
@layer utilities{.summary .nested-leaf:not(button){color:rgb(0 0 0 / .6)!important}}
@layer components{._cadencedShimmer_fixture_2,._cadencedShimmer_fixture_2 *{color:rgb(0 0 0 / .385)!important;-webkit-text-fill-color:currentColor}}
._cadencedShimmerSweep_fixture_35{position:absolute;inset:0;pointer-events:none}
.text-secondary{color:rgb(0 0 0 / .65)}.text-tertiary{color:rgb(0 0 0 / .49)}
html[data-theme=dark] .text-secondary{color:rgb(255 255 255 / .65)}
.row>span{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}svg{flex-shrink:0}
button{font:inherit;color:inherit;border:0;padding:0;background:transparent;text-align:left}button[aria-expanded]{position:absolute;inset:0;cursor:pointer}
.group\\/activity-header{position:relative}.time button{display:flex;gap:8px;align-items:center}
.excluded{position:relative;z-index:1;display:flex;gap:30px;margin-top:18px}.disabled{opacity:.4}
</style><div id="root"><main><div class="backdrop" data-ccc-wallpaper-exclude></div>
<div id="edit" class="row group/activity-header"><button aria-expanded="false" aria-label="展开"></button><span class="contents text-text/60">'''+icon+'''</span><span class="summary"><span class="text-text/60"><span class="nested-leaf">编辑了文件运行了命令</span></span></span></div>
<div id="agent" class="row group/activity-header">'''+icon+'''<span class="summary"><span class="text-text/60"><span class="_cadencedShimmer_fixture_2"><span class="running-leaf">已发送消息1 个智能体</span><span class="_cadencedShimmerSweep_fixture_35" aria-hidden="true">已发送消息1 个智能体</span></span></span></span></div>
<div id="duration" class="row time text-size-chat text-secondary"><button class="text-size-chat text-tertiary"><span>用时 16 分钟 22 秒</span>'''+icon+'''</button></div>
<div class="excluded"><nav><span id="nav" class="text-secondary">侧栏名称</span></nav><span data-slot="thread-summary-panel-item"><span id="panel" class="text-secondary">侧栏面板</span></span><div data-markdown-text-style="assistant-message"><span id="body-text" class="text-secondary">原生正文</span></div></div>
<div class="excluded"><button id="ordinary" class="text-secondary">普通按钮</button><div class="group/activity-header disabled"><button id="disabled" aria-disabled="true">不可用</button></div><span id="colored" style="color:rgb(200,30,20)">错误</span></div>
</main></div>'''
checks=[];errors=[]
with sync_playwright() as pw:
    browser=pw.chromium.launch(channel='msedge',headless=True)
    try:
        page=browser.new_page(device_scale_factor=1.5);page.on('pageerror',lambda e:errors.append(str(e)))
        for theme,width,height in [('light',2560,1440),('dark',2560,1440),('light',760,980)]:
            page.set_viewport_size({'width':width,'height':height});page.set_content(fixture);page.evaluate('v=>document.documentElement.dataset.theme=v',theme)
            excluded="['nav','panel','body-text','ordinary','colored','disabled'].map(id=>{const s=getComputedStyle(document.getElementById(id));return [s.color,s.fontWeight,s.textShadow,s.opacity]})"
            native=page.evaluate(excluded)
            # Baseline and result share exactly the same wallpaper substrate.
            page.evaluate(apply);page.wait_for_function("window.__cccWallpaperV1?.diagnostic().state==='applied'")
            if args.wallpaper_root:page.evaluate("document.querySelector('.backdrop').remove()")
            else:page.add_style_tag(content='#ccc-wallpaper-layer{visibility:hidden!important}')
            page.evaluate("window.__contrastRules=[...document.getElementById('ccc-wallpaper-style').sheet.cssRules].flatMap(r=>r.cssRules?[...r.cssRules]:[r]).filter(r=>r.selectorText?.includes('group/activity-header')).map(r=>({rule:r,css:r.style.cssText}));window.__contrastRules.forEach(v=>v.rule.style.cssText='')")
            page.screenshot(path=str(out/f'{theme}-{width}-before.png'))
            before=Image.open(io.BytesIO(page.screenshot())).convert('RGB');page.evaluate('window.__contrastRules.forEach(v=>v.rule.style.cssText=v.css)')
            after=Image.open(io.BytesIO(page.screenshot())).convert('RGB');after.save(out/f'{theme}-{width}-after.png')
            value=page.evaluate('''()=>{
                const rgba=color=>{const c=document.createElement('canvas');c.width=c.height=1;const x=c.getContext('2d');x.fillStyle=color;x.fillRect(0,0,1,1);return [...x.getImageData(0,0,1,1).data]};
                const s=e=>{const c=getComputedStyle(e),r=e.getBoundingClientRect(),p=getComputedStyle(e,'::before');return {color:rgba(c.color),weight:c.fontWeight,shadow:c.textShadow,background:c.backgroundColor,image:c.backgroundImage,boxShadow:c.boxShadow,before:p.content,filter:c.filter,height:r.height}};
                return {row:s(document.getElementById('edit')),text:s(document.querySelector('#edit .nested-leaf')),running:s(document.querySelector('.running-leaf')),icon:s(document.querySelector('#edit svg')),duration:s(document.querySelector('#duration button'))};}''')
            assert value['row']['color']==value['text']['color']==value['running']['color']==value['duration']['color']==value['icon']['color']==[17,17,17,255],value
            assert value['row']['weight']=='400' and value['text']['weight']=='400',value
            assert value['row']['background']=='rgba(0, 0, 0, 0)' and value['row']['image']=='none' and value['row']['boxShadow']=='none' and value['row']['before']=='none',value
            assert value['row']['height']==32 and value['text']['shadow']=='none' and value['icon']['filter']=='none',value
            assert page.evaluate(excluded)==native,'Changed unrelated controls'
            # Blank pixels directly around the letters remain the same wallpaper.
            for x,y in [(38,82),(400,138),(600,194)]:
                point=round(x*1.5),round(y*1.5);assert before.getpixel(point)==after.getpixel(point),(point,'New background or glow')
            assert max(ch[1] for ch in ImageChops.difference(before,after).getextrema())>30,'No visible contrast change'
            assert page.evaluate("(()=>{const e=document.querySelector('#edit button'),r=e.getBoundingClientRect();return document.elementFromPoint(r.x+4,r.y+4)===e})()")
            # Streaming rows pick up CSS immediately, independent of a DOM scan.
            assert page.evaluate('''()=>{const e=document.getElementById('agent').cloneNode(true);e.id='new-row';document.querySelector('main').append(e);return getComputedStyle(e).color===getComputedStyle(document.getElementById('agent')).color}''')
            page.evaluate(disable)
            assert page.evaluate("getComputedStyle(document.getElementById('edit')).textShadow")=='none'
            checks.append({'theme':theme,'viewport':[width,height],'scale':1.5,'leafColor':[17,17,17,255],'nativeImportantLayerOverridden':True,'runningLeafNoFade':True,'nativeWeight':True,'noGlyphOutlineOrGlow':True,'noAddedBackground':True,'nativeControlsAndClickTargetsPreserved':True,'dynamicAndDisable':True})
        assert not errors,errors
        result={'success':True,'checks':checks,'pageErrors':errors,'mouseOrKeyboardControl':False,'inferenceRequests':0}
        (out/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
    finally:browser.close()
