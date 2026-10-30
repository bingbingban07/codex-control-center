"""Compiled renderer acceptance in isolated headless Edge; no user input or inference.

Only the fixture copy's origin guard is adapted to about:blank. Production guards remain
unchanged. All sample text is created here; no user conversation is inspected.
"""
import argparse, io, json, pathlib, re, statistics
from PIL import Image
from playwright.sync_api import sync_playwright

p=argparse.ArgumentParser()
p.add_argument('--script',required=True);p.add_argument('--disable',required=True);p.add_argument('--out',required=True)
p.add_argument('--art-script');p.add_argument('--previous-script')
a=p.parse_args();out=pathlib.Path(a.out).resolve();out.mkdir(parents=True,exist_ok=True)
guard="location.protocol!=='app:'||location.hostname!=='-'||location.pathname!=='/index.html'"
def read(path):
    s=pathlib.Path(path).read_text(encoding='utf-8-sig');assert s.count(guard)==1
    return s.replace(guard,"location.protocol!=='about:'")
def parameters(script,**values):
    match=re.search(r'const p=(\{[^\n]+\}), key=',script);assert match
    payload=json.loads(match[1]);payload.update(values)
    return script[:match.start(1)]+json.dumps(payload,separators=(',',':'))+script[match.end(1):]
script=read(a.script);disable=read(a.disable);checks=[]
fixture='''<style>
*{box-sizing:border-box}body{margin:0;font:20px/1.6 Arial;color:#222}
#root{height:100vh;background:var(--color-surface);--color-surface:rgb(255,255,255)}
main{height:100vh;padding:30px;background:var(--color-surface)}
p{margin:20px 0}#short{width:300px}#wrap{width:250px}
#scroller{position:absolute;left:500px;top:100px;width:350px;height:180px;overflow:auto}
#scroller p{height:40px;margin:0;width:330px}
aside{position:absolute;left:30px;top:520px}pre{position:absolute;left:500px;top:360px;background:rgb(230,230,230)}
#dialog{position:absolute;left:500px;top:450px;background:white}
</style><div id="root"><main>
<p id="short">A calm reading surface</p><p id="wrap">Multiple lines belong to one quiet paragraph rather than separate labels.</p>
<p id="inline">A <b>bold</b> and <span>normal</span> line</p>
<div id="scroller">'''+''.join(f'<p id="row{i}">Reading row {i}</p>' for i in range(80))+'''</div>
<aside><p id="sidebar">Native sidebar name</p></aside>
<pre id="code"><code>const preserved = true;</code></pre>
<div role="dialog" id="dialog"><p id="dialog-text">Native dialog surface</p></div>
<button id="button" style="position:absolute;left:500px;top:600px">Native target</button>
</main></div>'''
with sync_playwright() as pw:
    browser=pw.chromium.launch(channel='msedge',headless=True)
    try:
        page=browser.new_page(viewport={'width':1100,'height':800},device_scale_factor=1.5)
        errors=[];page.on('pageerror',lambda e:errors.append(str(e)));page.set_content(fixture)
        page.evaluate('''(()=>{window.__scrollAdds=0;window.__rangeReads=0;
          const add=EventTarget.prototype.addEventListener;EventTarget.prototype.addEventListener=function(type,...args){if(type==='scroll')window.__scrollAdds++;return add.call(this,type,...args)};
          const range=Document.prototype.createRange;Document.prototype.createRange=function(...args){window.__rangeReads++;return range.apply(this,args)};})()''')
        before=page.evaluate("[...document.querySelectorAll('p,li,pre,button')].map(e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height,e.childNodes.length]})")
        def apply(s=script):
            page.evaluate(s);page.wait_for_timeout(120)
        def color(selector):
            return page.evaluate('''selector=>{const e=document.querySelector(selector),s=getComputedStyle(e),c=document.createElement('canvas');c.width=c.height=1;
              const ctx=c.getContext('2d'),plate=getComputedStyle(e,'::before');ctx.fillStyle=plate.content==='\"\"'?plate.backgroundColor:s.backgroundColor;ctx.fillRect(0,0,1,1);return [...ctx.getImageData(0,0,1,1).data]}''',selector)
        def screenshot():return Image.open(io.BytesIO(page.screenshot())).convert('RGB')
        apply(parameters(script,overlay=0))
        base=screenshot()
        apply(parameters(script,overlay=.5))
        active=screenshot()
        assert active.getpixel((int(280*1.5),int(65*1.5)))!=base.getpixel((int(280*1.5),int(65*1.5)))
        assert active.getpixel((1600,1150))==base.getpixel((1600,1150))
        assert 98<=color('#short')[3]<=100
        assert page.evaluate("getComputedStyle(document.getElementById('short'),'::before').borderRadius")=='8px'
        assert page.evaluate("getComputedStyle(document.querySelector('#wrap')).boxShadow")=='none'
        assert page.evaluate("getComputedStyle(document.querySelector('#wrap'),'::before').left")=='-10px'
        assert page.evaluate("getComputedStyle(document.querySelector('#wrap')).outlineStyle")=='none'
        assert color('#inline span')[3]==0 and color('#sidebar')[3]==0 and color('#dialog-text')[3]==0
        assert page.evaluate("getComputedStyle(document.getElementById('code')).backgroundColor")=='rgb(230, 230, 230)'
        after=page.evaluate("[...document.querySelectorAll('p,li,pre,button')].map(e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height,e.childNodes.length]})")
        assert before==after,'Protection changed layout or text DOM'
        assert page.evaluate("(()=>{const e=document.getElementById('button'),r=e.getBoundingClientRect();return document.elementFromPoint(r.x+5,r.y+5)===e})()")
        active.save(out/'reading-surface-light.png')
        checks.append({'singleReadingOwner':True,'noGlow':True,'verticalExpansionCssPixels':6,'horizontalExpansionCssPixels':10,'noInlineLabels':True,'nativeSidebarCodeDialogPreserved':True,'blankAreasTransparent':True,'layoutAndTextDomUnchanged':True,'scale':1.5})
        # Check actual rendered pixels at rapidly changed scroll positions. The same paragraph
        # owns both its text and its background; no delayed viewport correction participates.
        positions=[0,85,360,900,1750,2650,2800,1600,700,200,25,0]
        for position in positions:
            point=page.evaluate('''position=>{const scroller=document.getElementById('scroller');scroller.scrollTop=position;
              const rows=[...scroller.querySelectorAll('p')],clip=scroller.getBoundingClientRect();
              const row=rows.find(e=>{const r=e.getBoundingClientRect();return r.top>clip.top+5&&r.bottom<clip.bottom-5});
              const r=row.getBoundingClientRect();return {x:r.right-20,y:r.top+20}}''',position)
            painted=screenshot();sample=(int(point['x']*1.5),int(point['y']*1.5))
            assert sum(painted.getpixel(sample))>sum(base.getpixel(sample))+30,(position,sample)
        motion=page.evaluate('''async()=>{const scroller=document.getElementById('scroller');let mismatches=0;const gaps=[];let last=performance.now();
          for(let frame=0;frame<120;frame++){await new Promise(requestAnimationFrame);const now=performance.now();gaps.push(now-last);last=now;
            scroller.scrollTop=(frame%60)*43;const p=scroller.querySelectorAll('p')[Math.min(79,Math.ceil(scroller.scrollTop/40)+1)];
            if(getComputedStyle(p,'::before').backgroundColor==='rgba(0, 0, 0, 0)')mismatches++;
          }return {frames:120,mismatches,p95FrameMs:gaps.sort((a,b)=>a-b)[Math.floor(gaps.length*.95)],scrollListeners:window.__scrollAdds,rangeReads:window.__rangeReads,
            canvases:document.querySelectorAll('#ccc-wallpaper-layer canvas').length,mode:window.__cccWallpaperV1.diagnostic().maskMode}}''')
        assert motion['mismatches']==0 and motion['scrollListeners']==0 and motion['rangeReads']==0 and motion['canvases']==0,motion
        assert motion['mode']=='single-response-surface'
        checks.append({'rapidScrollPixelPositions':len(positions),'nativeScroll':motion})
        # Streaming updates and new paragraphs receive CSS protection in the very same turn.
        assert page.evaluate("(()=>{const p=document.createElement('p');p.id='streaming';p.textContent='Synthetic update';document.querySelector('main').append(p);p.firstChild.data+=' extended';return getComputedStyle(p,'::before').backgroundColor!=='rgba(0, 0, 0, 0)'})()")
        page.evaluate("document.getElementById('root').style.setProperty('--color-surface','rgb(24,24,24)');document.body.style.color='#eee'")
        page.wait_for_timeout(950)
        assert all(abs(v-24)<=2 for v in color('#short')[:3])
        page.screenshot(path=str(out/'reading-surface-dark.png'))
        page.set_viewport_size({'width':760,'height':980})
        assert color('#short')[3]>0
        page.evaluate("(()=>{const f=document.createElement('section');f.id='long';const frag=document.createDocumentFragment();for(let i=0;i<10000;i++){const p=document.createElement('p');p.textContent='Synthetic row';frag.append(p)}f.append(frag);document.querySelector('main').append(f)})()")
        page.wait_for_timeout(800)
        diagnostic=page.evaluate('window.__cccWallpaperV1.diagnostic()')
        assert diagnostic['scanned']<=3001 and diagnostic['scanMs']<250,diagnostic
        assert page.evaluate("getComputedStyle(document.querySelector('#long p:last-child'),'::before').backgroundColor!=='rgba(0, 0, 0, 0)'")
        page.evaluate("document.getElementById('long').remove()")
        checks.append({'dynamicParagraphImmediate':True,'darkTheme':True,'narrowResize':True,'longLayoutNodes':10000,'surfaceScanMs':diagnostic['scanMs']})
        apply(parameters(script,overlay=0));assert color('#short')[3]==0
        apply(parameters(script,overlay=1));assert 198<=color('#short')[3]<=200
        for _ in range(12):apply()
        assert page.locator('#ccc-wallpaper-layer').count()==1 and page.locator('#ccc-wallpaper-style').count()==1
        page.evaluate(disable);page.wait_for_timeout(200)
        assert page.locator('#ccc-wallpaper-layer').count()==0 and page.locator('[data-ccc-wallpaper-surface]').count()==0
        assert color('#short')[3]==0 and not errors,errors
        checks.append({'zeroAndMaximumStrength':True,'reapplyCleanup':12,'disabledRestoresNativeStyles':True,'pageErrors':errors})
        # Visual review fixture: original wallpaper, invented content, same desktop scale.
        if a.art_script:
            art=parameters(read(a.art_script),opacity=1,overlay=.5)
            page.set_viewport_size({'width':1700,'height':1050})
            page.set_content('''<style>
              *{box-sizing:border-box}body{margin:0;font:20px/1.75 "Segoe UI","Microsoft YaHei UI",sans-serif;color:#242424}
              #root{height:100vh;background:var(--color-surface);--color-surface:rgb(255,255,255)}
              .shell{display:flex;height:100vh;background:var(--color-surface)}
              aside{width:285px;background:rgba(255,255,255,.78);border-right:1px solid #ffffff70;padding:34px 24px}
              aside p{font-size:18px;margin:22px 0}main{flex:1;padding:65px 90px;background:var(--color-surface)}
              article{max-width:840px;margin:80px auto}h2{font-size:27px;font-weight:600;margin:0 0 26px}p{margin:22px 0}
              .tag{font-size:13px;letter-spacing:2px;color:#666} .native-card{background:rgba(255,255,255,.8);border:1px solid #ffffff90;border-radius:18px;padding:24px;margin-top:30px}
              .composer{position:absolute;left:375px;right:90px;bottom:36px;border-radius:24px;background:#fffffff2;border:1px solid #0000000d;box-shadow:0 4px 24px #00000008;padding:24px;color:#888}
              html[data-theme="dark"] aside{background:rgba(24,24,24,.84);border-color:#ffffff15}
              html[data-theme="dark"] .native-card{background:rgba(32,32,32,.86);border-color:#ffffff20}
              html[data-theme="dark"] .composer{background:#202020ed;border-color:#ffffff15}
              html[data-theme="dark"] .tag{color:#aaa}
            </style><div id="root"><div class="shell"><aside><strong>Codex</strong><p>＋ 新聊天</p><p>置顶</p><p>桌面应用开发</p><p>项目与工作空间</p><p>外观与壁纸</p></aside><main>
              <article><div class="tag">WORKSPACE / APPEARANCE</div><h2>让壁纸成为背景，让内容清晰呈现。</h2>
              <p>阅读区域采用轻柔的半透明底色，边缘自然过渡。段落保持完整，文字不再被分割成一条条醒目的白色标签。</p>
              <p>滚动时，保护底色与文字始终同行。侧栏、按钮和原生卡片保持各自的层次，留白处继续呈现你选择的壁纸。</p>
              <div class="native-card">已应用 · 柔和阅读保护<br><span style="font-size:15px;color:#777">深浅主题自适应 · 无需定位动画</span></div></article>
              <div class="composer">和 Codex 一起工作…<div style="font-size:14px;margin-top:26px">＋　完全访问</div></div>
            </main></div></div>''')
            if a.previous_script:
                old=read(a.previous_script);payload=json.loads(re.search(r'const p=(\{[^\n]+\}), key=',art)[1]);old=parameters(old,**payload)
                apply(old);page.screenshot(path=str(out/'appearance-before.png'))
            apply(art);page.screenshot(path=str(out/'appearance-after.png'))
            page.evaluate("document.getElementById('root').style.setProperty('--color-surface','rgb(24,24,24)');document.body.style.color='#eee';document.documentElement.dataset.theme='dark'")
            page.wait_for_timeout(950);page.screenshot(path=str(out/'appearance-dark.png'))
            checks.append({'appearanceFixture':'invented content with user wallpaper','beforeAfterScreenshots':bool(a.previous_script),'desktopScale':1.5})
        result={'success':True,'checks':checks,'mouseOrKeyboardControl':False,'inferenceRequests':0,'testOriginGuardSubstitutionOnly':True}
        (out/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
    finally:browser.close()
