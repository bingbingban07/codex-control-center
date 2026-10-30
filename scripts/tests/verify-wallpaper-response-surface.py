"""Pixel regression for the user's dense paragraph/list overlap report.

Runs compiled scripts in an isolated headless Edge. No user input, conversations,
model calls, or production origin guard changes.
"""
import argparse,base64,io,json,pathlib,re
from PIL import Image
from playwright.sync_api import sync_playwright

p=argparse.ArgumentParser();p.add_argument('--script',required=True);p.add_argument('--disable',required=True);p.add_argument('--previous',required=True);p.add_argument('--out',required=True)
a=p.parse_args();out=pathlib.Path(a.out).resolve();out.mkdir(parents=True,exist_ok=True)
guard="location.protocol!=='app:'||location.hostname!=='-'||location.pathname!=='/index.html'"
def load(path):
    s=pathlib.Path(path).read_text(encoding='utf-8-sig');assert s.count(guard)==1
    return s.replace(guard,"location.protocol!=='about:'")
def parameters(s,**fields):
    m=re.search(r'const p=(\{[^\n]+\}), key=',s);assert m
    value=json.loads(m[1]);value.update(fields)
    return s[:m.start(1)]+json.dumps(value,separators=(',',':'))+s[m.end(1):]
script,disable,previous=map(load,[a.script,a.disable,a.previous])
fixture='''<style>
*{box-sizing:border-box}body{margin:0;font:20px/1.625 "Segoe UI","Microsoft YaHei UI",sans-serif;color:#202020}
#root{min-height:100vh;background:var(--color-surface);--color-surface:rgb(255,255,255)}
html[data-theme=dark] #root{--color-surface:rgb(24,24,24);color:#eee}
main{height:100vh;overflow:auto;padding:70px 70px;background:var(--color-surface)}
article{width:100%;max-width:1200px;margin:0 auto 50px}
[data-markdown-text-style]{margin:0;padding:0;line-height:1.625}
p{margin:0 0 4px}p:last-child{margin:14px 0 0}ul,ol{margin:0;padding-left:32.5px}
li{padding-left:7.5px;margin:0}li>p{margin:0}blockquote{margin:12px 0;padding-left:16px;border-left:3px solid #8884}
pre{background:rgb(230,230,230);padding:12px;border-radius:8px;color:#222}
#outgoing{margin:35px 0 0 30%;background:#ef74b6;color:white;border-radius:24px;padding:8px 18px}
footer{margin:24px 0}button{font:inherit}a{color:#1264b5}
</style><div id="root"><main>
<article><div data-markdown-text-style="assistant-message" id="reply">
<p>已更新到 <strong>0.8.6</strong>：</p>
<ul><li>发送气泡保留原生样式，不再额外叠加白底。</li><li>阅读底色取消泛光，连续列表共用一块平整背景。</li><li>段落、列表和它们之间的间隙亮度一致。</li><li>图片使用压暗模式：0% 保留原图，100% 全黑。</li></ul>
<p>当前窗口可以热更新，<strong>无需重启</strong>；字体与行距保持原样。</p>
</div><div id="outgoing" data-user-message-bubble><p>发送消息保持原生气泡。</p></div></article>
<article><div data-message-author-role="assistant"><div id="nested" data-markdown-copy data-markdown-text-style="assistant-message">
<h2>嵌套内容</h2><ol><li><p>列表内的段落</p><ul><li>嵌套列表项</li><li>另一项</li></ul></li><li>普通列表项</li></ol>
<blockquote><p>引用中的段落</p><ul><li>引用中的列表</li></ul></blockquote>
<pre><code>const native = true;</code></pre><p><a id="link" href="#">保留链接交互</a>与正常选中。</p>
</div></div></article><footer><button id="native-button">原生控件</button></footer>
</main></div>'''
checks=[];errors=[]
with sync_playwright() as pw:
    browser=pw.chromium.launch(channel='msedge',headless=True)
    try:
        page=browser.new_page(device_scale_factor=1.5);page.on('pageerror',lambda e:errors.append(str(e)))
        for theme,width,height,zoom in [('light',2560,1440,1),('light',1100,900,1.125),('dark',1100,900,1.125),('light',760,980,1)]:
            page.set_viewport_size({'width':width,'height':height});page.set_content(fixture)
            page.evaluate('p=>{document.documentElement.dataset.theme=p.theme;document.body.style.zoom=p.zoom}',{'theme':theme,'zoom':zoom})
            geometry="[...document.querySelectorAll('article,article *,button')].map(e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height,e.childNodes.length]})"
            native=page.evaluate(geometry)
            if theme=='light' and width==1100:
                page.evaluate(parameters(previous,overlay=.8));page.wait_for_timeout(100)
                page.screenshot(path=str(out/'dense-list-before.png'))
            page.evaluate(parameters(script,overlay=.8));page.wait_for_function("window.__cccWallpaperV1?.diagnostic().state==='applied'")
            assert page.evaluate(geometry)==native,'Changed wrapping, line spacing, or DOM'
            styles=page.evaluate('''()=>{
                const owner=document.getElementById('reply'),s=getComputedStyle(owner,'::before');
                const visiblePlate=e=>{const p=getComputedStyle(e,'::before');return p.content!=='none'&&p.content!=='normal'&&p.backgroundColor!=='rgba(0, 0, 0, 0)'};
                return {ownerPlate:visiblePlate(owner),nestedOwnerPlate:visiblePlate(document.getElementById('nested')),
                    decoratedChildren:[...document.querySelectorAll('#reply *,#nested *')].filter(e=>visiblePlate(e)||getComputedStyle(e).outlineStyle!=='none'||getComputedStyle(e).boxShadow!=='none').length,
                    duplicateOuterPlate:visiblePlate(document.getElementById('nested').parentElement),
                    outgoingPlate:visiblePlate(document.getElementById('outgoing')),outgoingTextPlate:visiblePlate(document.querySelector('#outgoing p')),
                    content:s.content,radius:s.borderRadius,shadow:s.boxShadow,outline:s.outlineStyle,inset:[s.top,s.right,s.bottom,s.left],pointerEvents:s.pointerEvents,
                    codeBackground:getComputedStyle(document.querySelector('pre')).backgroundColor};}''')
            assert styles['ownerPlate'] and styles['nestedOwnerPlate'] and styles['decoratedChildren']==0 and not styles['duplicateOuterPlate'],styles
            assert not styles['outgoingPlate'] and not styles['outgoingTextPlate'],styles
            assert styles['shadow']=='none' and styles['outline']=='none' and styles['radius']=='8px' and styles['pointerEvents']=='none',styles
            assert styles['codeBackground']=='rgb(230, 230, 230)',styles
            if theme=='light' and width==1100:page.screenshot(path=str(out/'dense-list-after.png'))
            page.screenshot(path=str(out/f'{theme}-{width}-zoom{zoom}.png'))
            # Remove wallpaper texture only in this fixture so actual screenshot
            # pixels expose any extra alpha layer, stripe or capsule at list seams.
            page.evaluate("document.querySelector('#ccc-wallpaper-layer img').style.visibility='hidden';document.getElementById('ccc-wallpaper-layer').style.cssText+=';background:rgb(60,80,100);filter:none'")
            # Glyphs are hidden only for this paint sample, after the normal
            # screenshots and geometry checks, so they cannot look like seams.
            page.add_style_tag(content='#reply,#reply *{color:transparent!important;text-shadow:none!important}')
            r=page.evaluate("(()=>{const r=document.getElementById('reply').getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height}})()")
            painted=Image.open(io.BytesIO(page.screenshot())).convert('RGB');samples=[]
            for offset in [2,-5]:
                column=[painted.getpixel((round((r['x']+offset*zoom)*1.5),y)) for y in range(round((r['y']+10*zoom)*1.5),round((r['y']+r['h']-10*zoom)*1.5))]
                assert all(max(c[i] for c in column)-min(c[i] for c in column)<=2 for i in range(3)),(theme,width,offset,'seam/overlap',column[:5])
                samples.append(column[len(column)//2])
            assert max(abs(x-y) for x,y in zip(*samples))<=2,'Expansion composited more than once'
            assert page.evaluate("(()=>{const a=document.getElementById('link'),r=a.getBoundingClientRect();return r.top>innerHeight||document.elementFromPoint(r.x+3,r.y+4)===a})()")
            # Streaming paragraph/list insertion changes no ownership and needs
            # no queued scan to assign the new children a separate background.
            assert page.evaluate('''()=>{const e=document.createElement('li');e.textContent='流式新增';document.querySelector('#reply ul').append(e);
                return getComputedStyle(e,'::before').content==='none'&&getComputedStyle(document.getElementById('reply'),'::before').content==='""'}''')
            for _ in range(6):page.evaluate(parameters(script,overlay=.8))
            assert page.locator('#ccc-wallpaper-layer').count()==1
            page.evaluate(disable)
            assert page.evaluate("getComputedStyle(document.getElementById('reply'),'::before').content")=='none'
            assert page.evaluate("getComputedStyle(document.getElementById('reply')).isolation")=='auto'
            checks.append({'theme':theme,'viewport':[width,height],'zoom':zoom,'scale':1.5,'singleResponsePlate':True,'nestedListAndParagraphNoExtraPaint':True,'seamPixelRangeAtMost':2,'geometryAndTextDomUnchanged':True,'outgoingAndCodePreserved':True,'streamingAndDisable':True})
        assert not errors,errors
        result={'success':True,'checks':checks,'pageErrors':errors,'mouseOrKeyboardControl':False,'inferenceRequests':0}
        (out/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
    finally:browser.close()
