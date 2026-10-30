"""Render the compiled wallpaper script in isolated headless Edge; no user input or inference.

The fixture alone substitutes the app:// origin guard so the unchanged renderer body can run
in about:blank. No existing browser or Codex page is connected to or inspected.
"""
import argparse
import json
import pathlib
from playwright.sync_api import sync_playwright

p = argparse.ArgumentParser()
p.add_argument('--script', required=True)
p.add_argument('--disable', required=True)
p.add_argument('--out', required=True)
a = p.parse_args()
out = pathlib.Path(a.out).resolve()
out.mkdir(parents=True, exist_ok=True)
guard = "location.protocol!=='app:'||location.hostname!=='-'||location.pathname!=='/index.html'"

def fixture_script(path):
    script = pathlib.Path(path).read_text(encoding='utf-8-sig')
    assert script.count(guard) == 1
    return script.replace(guard, "location.protocol!=='about:'")

script = fixture_script(a.script)
disable = fixture_script(a.disable)
checks = []
with sync_playwright() as pw:
    browser = pw.chromium.launch(channel='msedge', headless=True)
    try:
        page = browser.new_page(viewport={'width': 1100, 'height': 800}, device_scale_factor=1.5)
        errors = []
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.set_content('''<style>
          *{box-sizing:border-box} body{margin:0;font:20px Arial;color:#222}
          #root{height:100vh;background:var(--color-surface);--color-surface:rgb(255,255,255)}
          main{height:100vh;padding:30px;background:var(--color-surface)}
          p{margin:20px 0} #wrap{width:230px} #scroller{position:absolute;left:500px;top:100px;width:350px;height:140px;overflow:auto}
          #scroller p{height:40px;margin:0} pre{position:absolute;top:480px;background:rgb(230,230,230)}
          #dialog{position:absolute;left:500px;top:450px;background:white}
        </style><div id="root"><main>
          <p id="short">Short line</p><p id="wrap">A multiline fixture that wraps across several visible lines.</p>
          <p id="inline">A <b>bold</b> and <span>normal</span> line</p>
          <div id="scroller"><p>Visible scroll row</p><p>Second row</p><p>Third row</p><p id="last">Last scroll row</p><p>End row</p></div>
          <pre id="code"><code>Protected code block</code></pre>
          <div role="dialog" id="dialog">Protected dialog</div>
          <button id="button" style="position:absolute;top:600px">Native target</button>
        </main></div>''')
        def apply(value=script):
            page.evaluate(value)
            page.wait_for_timeout(180)
        def diag():
            return page.evaluate('window.__cccWallpaperV1.diagnostic()')
        def alpha(x, y):
            return page.evaluate('''([x,y])=>{const c=document.querySelector('#ccc-wallpaper-layer canvas');
              const s=c.width/innerWidth;return c.getContext('2d').getImageData(Math.floor(x*s),Math.floor(y*s),1,1).data[3]}''', [x,y])
        def rect(selector):
            return page.evaluate('''selector=>{const e=document.querySelector(selector),r=document.createRange();r.selectNodeContents(e);
              return [...r.getClientRects()].map(v=>({x:v.x,y:v.y,width:v.width,height:v.height}))}''', selector)
        apply()
        r = rect('#short')[0]
        assert 35 <= alpha(r['x']+2, r['y']+2) <= 40
        assert alpha(1000, r['y']+2) == 0 and alpha(1090,790) == 0 and alpha(10,10) == 0
        assert len(rect('#wrap')) >= 2
        for line in rect('#wrap'):
            assert alpha(line['x']+2,line['y']+2) > 0
        checks.append({'localLineProtection':True,'blankAreaTransparent':True,'wraps':True,'scale':1.5})
        for selector in ['#code','#dialog']:
            line=rect(selector)[0]
            assert alpha(line['x']+2,line['y']+2) == 0
        assert page.evaluate("getComputedStyle(document.getElementById('code')).backgroundColor") == 'rgb(230, 230, 230)'
        assert page.evaluate("(()=>{const e=document.getElementById('button'),r=e.getBoundingClientRect();return document.elementFromPoint(r.x+5,r.y+5)===e})()")
        assert page.evaluate("getComputedStyle(document.getElementById('ccc-wallpaper-layer'),'::after').content") in ['none','normal']
        checks.append({'nativeCodeAndDialogPreserved':True,'clickThrough':True,'noFullPagePseudoMask':True})
        # Overlapping inline rectangles must not compound the configured alpha.
        assert page.evaluate("(()=>{const c=document.querySelector('#ccc-wallpaper-layer canvas'),v=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let m=0;for(let i=3;i<v.length;i+=4)m=Math.max(m,v[i]);return m})()") <= 40
        page.evaluate("document.getElementById('scroller').scrollTop=100")
        page.wait_for_timeout(180)
        r=rect('#last')[0]
        assert 100 <= r['y'] < 240 and alpha(r['x']+2,r['y']+2)>0
        assert alpha(510,245)==0
        page.evaluate("document.getElementById('short').firstChild.data='Changed';document.getElementById('short').style.marginLeft='80px'")
        page.wait_for_timeout(950)
        r=rect('#short')[0]
        assert alpha(r['x']+2,r['y']+2)>0 and alpha(32,r['y']+2)==0
        old_x=r['x']+2
        page.evaluate("document.getElementById('short').style.marginLeft='160px'")
        page.wait_for_timeout(180)
        r=rect('#short')[0]
        assert alpha(r['x']+2,r['y']+2)>0 and alpha(old_x,r['y']+2)==0
        checks.append({'scrollRepositionsMask':True,'scrollClip':True,'characterUpdates':True,'attributeLayoutUpdates':True})
        page.screenshot(path=str(out/'local-mask-light.png'))
        page.evaluate("document.getElementById('root').style.setProperty('--color-surface','rgb(24,24,24)')")
        page.wait_for_timeout(950)
        r=rect('#short')[0]
        rgb=page.evaluate('''([x,y])=>{const c=document.querySelector('#ccc-wallpaper-layer canvas'),s=c.width/innerWidth;
          return [...c.getContext('2d').getImageData(Math.floor(x*s),Math.floor(y*s),1,1).data]}''',[r['x']+2,r['y']+2])
        assert all(abs(v-24)<5 for v in rgb[:3]),rgb
        page.screenshot(path=str(out/'local-mask-dark.png'))
        page.set_viewport_size({'width':760,'height':980})
        page.wait_for_timeout(950)
        assert page.evaluate("document.querySelector('#ccc-wallpaper-layer canvas').width") == 1140
        checks.append({'darkThemeMaskColor':rgb,'resize':True})
        page.evaluate("(()=>{const f=document.createElement('section');f.id='long';const fragment=document.createDocumentFragment();for(let i=0;i<10000;i++){const p=document.createElement('p');p.textContent='Synthetic row';fragment.append(p)}f.append(fragment);document.querySelector('main').append(f)})()")
        page.wait_for_timeout(1000)
        performance=diag()
        assert performance['maskLines'] <= 800 and performance['maskMs'] < 250,performance
        checks.append({'longLayoutNodes':10000,'maskLines':performance['maskLines'],'maskMs':performance['maskMs']})
        page.evaluate("document.getElementById('long').remove()")
        assert '"overlay":0.15' in script
        apply(script.replace('"overlay":0.15','"overlay":0'))
        assert diag()['maskLines'] == 0 and alpha(120,55)==0
        apply(script.replace('"overlay":0.15','"overlay":1'))
        r=rect('#short')[0]
        assert alpha(r['x']+2,r['y']+2)==255 and alpha(740,960)==0
        for _ in range(12): apply()
        assert page.locator('#ccc-wallpaper-layer').count()==1 and page.locator('#ccc-wallpaper-layer canvas').count()==1
        page.evaluate(disable)
        page.wait_for_timeout(250)
        assert page.locator('#ccc-wallpaper-layer').count()==0 and page.locator('[data-ccc-wallpaper-surface]').count()==0
        assert not errors,errors
        checks.append({'zeroAndMaximumStrength':True,'reapplyCleanup':12,'disabledCleanup':True,'pageErrors':errors})
        result={'success':True,'checks':checks,'mouseOrKeyboardControl':False,'inferenceRequests':0,'testOriginGuardSubstitutionOnly':True}
        (out/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps(result,ensure_ascii=False))
    finally:
        browser.close()
