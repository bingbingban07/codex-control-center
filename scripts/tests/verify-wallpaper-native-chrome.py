"""Render translucent chrome and native control regressions in an isolated browser.

Uses invented content and the production renderer (only its app origin guard is
adapted to about:blank). Does not send input to a user window or call a model.
"""
import argparse
import io
import json
import pathlib
import re

from PIL import Image, ImageChops, ImageStat
from playwright.sync_api import sync_playwright

parser = argparse.ArgumentParser()
parser.add_argument('--script', required=True)
parser.add_argument('--disable', required=True)
parser.add_argument('--out', required=True)
args = parser.parse_args()
output = pathlib.Path(args.out).resolve()
output.mkdir(parents=True, exist_ok=True)


def script(path):
    value = pathlib.Path(path).read_text(encoding='utf-8-sig')
    guard = "location.protocol!=='app:'||location.hostname!=='-'||location.pathname!=='/index.html'"
    assert value.count(guard) == 1
    return value.replace(guard, "location.protocol!=='about:'")


apply_script, disable_script = script(args.script), script(args.disable)
fixture = '''<style>
*{box-sizing:border-box}body{margin:0;color:#222;font:20px/1.5 "Segoe UI",sans-serif}
#root{isolation:isolate;height:100vh;background:var(--color-surface);--color-surface:rgb(255,255,255);--accent:#ed78b7}
html[data-theme=dark] #root{--color-surface:rgb(24,24,24);color:#eee}
main{height:100vh;background:var(--color-surface)}
header{position:absolute;left:0;right:0;top:0;height:78px;border-bottom:1px solid #8884;z-index:2;display:flex;align-items:center;padding:0 32px}
header::after{content:"";position:absolute;inset:0;pointer-events:none;background:linear-gradient(transparent,#8881)}
article{padding:130px 10%;max-width:1300px}article p{margin:24px 0}
[data-thread-scroll-footer]{position:absolute;bottom:0;left:0;right:0;padding:24px 8%;height:220px;z-index:2;pointer-events:none}
[data-thread-scroll-footer]::before{content:"";position:absolute;left:0;right:0;top:-24px;height:24px;pointer-events:none;background:linear-gradient(transparent,var(--color-surface))}
[data-thread-scroll-footer] > .pointer-events-none.absolute.bg-surface{position:absolute;inset:0;background:var(--color-surface);pointer-events:none;z-index:-1}
.native-top-fade{position:absolute;inset:78px 0 auto;height:40px;pointer-events:none;background:linear-gradient(var(--color-surface),transparent)}
#native-bottom-fade-holder{position:absolute;left:0;right:0;bottom:220px;height:32px;pointer-events:none}
#native-bottom-fade-holder>div{position:absolute;inset:0;background:linear-gradient(to top,var(--color-surface),transparent);pointer-events:none}
[data-codex-composer-root]{height:100%;pointer-events:auto}
[data-composer-surface-variant]{height:100%;background:var(--color-surface);border:1px solid #8884;border-radius:28px;box-shadow:0 3px 12px #0001;padding:24px}
[contenteditable]{height:65%;outline:none}[contenteditable] p{margin:0}
button{border:0;border-radius:50%;background:var(--accent);color:white;font:inherit;float:right;width:40px;height:40px}
#hint{margin:0;font-size:14px}body.expanded [data-thread-scroll-footer]{height:75vh}
</style><div id="root"><main class="thread-scroll-container">
<header data-app-shell-titlebar="true"><div data-app-shell-main-titlebar="true"><p>项目 · 原生标题栏</p></div></header>
<div data-app-shell-main-content-top-fade="visible"><div id="native-top-fade" class="_MainContentTopFade_fixture_2 native-top-fade"></div></div>
<article><h1>壁纸仅用于聊天背景</h1><p id="reading">这是用于验证的示例段落。</p></article>
<div id="native-bottom-fade-holder" class="pointer-events-none sticky"><div id="native-bottom-fade" class="pointer-events-none absolute from-surface"></div></div>
<div data-thread-scroll-footer="true"><div id="native-footer-background" class="pointer-events-none absolute bg-surface"></div><div data-codex-composer-root data-composer-placement="thread">
<div data-composer-surface-variant="default"><div contenteditable="true" data-codex-composer><p>保留原生输入框</p></div>
<p id="hint">默认模型 · 原生控件</p><button id="send">↑</button></div>
</div></div></main></div>'''
checks = []
errors = []
native_selector = 'header,header *,[data-thread-scroll-footer],[data-thread-scroll-footer] *'
style_expression = '''selector=>[...document.querySelectorAll(selector)].map(e=>{
  const s=getComputedStyle(e),r=e.getBoundingClientRect();return {
    tag:e.tagName,x:r.x,y:r.y,width:r.width,height:r.height,color:s.color,
    border:s.border,borderRadius:s.borderRadius,boxShadow:s.boxShadow,
    padding:s.padding,font:s.font,pointerEvents:s.pointerEvents,
    children:e.childNodes.length};})'''

with sync_playwright() as pw:
    browser = pw.chromium.launch(channel='msedge', headless=True)
    try:
        page = browser.new_page(device_scale_factor=1.5)
        page.on('pageerror', lambda error: errors.append(str(error)))
        for theme, width, height, expanded in [
            ('light', 2560, 1440, False), ('dark', 2560, 1440, True),
            ('light', 900, 980, True), ('dark', 900, 980, False)]:
            page.set_viewport_size({'width': width, 'height': height})
            page.set_content(fixture)
            page.evaluate('''p=>{document.documentElement.dataset.theme=p.theme;
                document.body.classList.toggle('expanded',p.expanded)}''',
                {'theme': theme, 'expanded': expanded})
            native_styles = page.evaluate(style_expression, native_selector)
            original = Image.open(io.BytesIO(page.screenshot())).convert('RGB')
            # A stale marker from an earlier injection must never expose a native control.
            page.evaluate("document.querySelector('[data-composer-surface-variant]').setAttribute('data-ccc-wallpaper-surface','1')")
            page.evaluate(apply_script)
            page.wait_for_function("window.__cccWallpaperV1?.diagnostic().state==='applied'")
            assert page.evaluate(style_expression, native_selector) == native_styles
            active = Image.open(io.BytesIO(page.screenshot())).convert('RGB')
            geometry = page.evaluate('''()=>{
                const e=document.querySelector('[data-composer-surface-variant]'),r=e.getBoundingClientRect();
                const header=getComputedStyle(document.querySelector('header'));
                const footer=getComputedStyle(document.querySelector('[data-thread-scroll-footer]'));
                const carrier=getComputedStyle(document.getElementById('native-footer-background'));
                return {x:r.x,y:r.y,w:r.width,h:r.height,footerY:document.querySelector('[data-thread-scroll-footer]').getBoundingClientRect().top,
                    headerImage:header.backgroundImage,headerColor:header.backgroundColor,headerBlur:header.backdropFilter,
                    footerImage:footer.backgroundImage,footerColor:footer.backgroundColor,carrierColor:carrier.backgroundColor,
                    nativeComposerColor:getComputedStyle(e).backgroundColor};}''')
            assert geometry['footerColor'] == geometry['carrierColor'] == 'rgba(0, 0, 0, 0)'
            assert geometry['headerImage'] == geometry['footerImage'] == 'none'
            assert '0.52' in geometry['headerColor']
            assert page.evaluate("['native-top-fade','native-bottom-fade'].every(id=>getComputedStyle(document.getElementById(id)).backgroundImage==='none')")
            assert geometry['headerBlur'] == 'blur(12px)'
            assert geometry['nativeComposerColor'] == ('rgb(255, 255, 255)' if theme == 'light' else 'rgb(24, 24, 24)')
            # Input card pixels retain native paint; the surrounding panels must
            # actually show the wallpaper, including the separate native carrier.
            pixel_differences = {}
            box = tuple(v*1.5 for v in (geometry['x']+32, geometry['y']+32,
                geometry['x']+geometry['w']-32, geometry['y']+geometry['h']-32))
            for name, box in [('composer-card', box)]:
                difference = ImageChops.difference(original.crop(box), active.crop(box))
                mean = max(ImageStat.Stat(difference).mean)
                # Chromium can change subpixel text antialiasing when a background
                # enters a compositing layer. Geometry/colors/styles must be exact;
                # native background pixels allow only one channel level of dithering.
                blank_box = tuple(v*1.5 for v in (geometry['x']+48,geometry['y']+8,geometry['x']+geometry['w']-48,geometry['y']+16))
                background = ImageChops.difference(original.crop(blank_box),active.crop(blank_box))
                background_max = max(channel[1] for channel in background.getextrema())
                if mean > .75 or background_max > 1:
                    original.crop(box).save(output / f'failed-{name}-native.png')
                    active.crop(box).save(output / f'failed-{name}-active.png')
                assert mean <= .75 and background_max <= 1, (theme, width, expanded, name, mean, background_max)
                pixel_differences[name] = {'meanChannelDifference': mean, 'backgroundMaxDifference': background_max}
            for box in [(width*.8*1.5,0,width*1.5,78*1.5), (0,geometry['footerY']*1.5,24,height*1.5)]:
                assert max(ImageStat.Stat(ImageChops.difference(original.crop(box),active.crop(box))).mean)>1, 'Panel remained an opaque native block'
            # A transparent gradient start cannot leave a white horizontal ledge.
            page.evaluate("document.querySelector('[data-thread-scroll-footer]').style.backgroundImage='none'")
            substrate=Image.open(io.BytesIO(page.screenshot())).convert('RGB')
            page.evaluate("document.querySelector('[data-thread-scroll-footer]').style.removeProperty('background-image')")
            for y in [geometry['footerY']-2,geometry['footerY']+2]:
                point=(15,round(y*1.5))
                assert max(abs(x-y) for x,y in zip(active.getpixel(point),substrate.getpixel(point)))<=2
            assert page.evaluate("getComputedStyle(document.getElementById('reading'),'::before').backgroundColor!=='rgba(0, 0, 0, 0)'")
            assert not page.evaluate("document.querySelector('header [data-ccc-wallpaper-surface],[data-thread-scroll-footer] [data-ccc-wallpaper-surface]')!==null")
            # CSS exclusions also protect controls inserted between throttled scans.
            dynamic = page.evaluate('''()=>{
                const e=document.querySelector('[data-composer-surface-variant]');
                e.setAttribute('data-ccc-wallpaper-surface','1');
                const p=document.createElement('p');p.textContent='Streaming control hint';e.append(p);
                return {background:getComputedStyle(e).backgroundColor,
                    textBackground:getComputedStyle(p).backgroundColor,
                    shadow:getComputedStyle(p).boxShadow};}''')
            assert dynamic['background'] == ('rgb(255, 255, 255)' if theme == 'light' else 'rgb(24, 24, 24)')
            assert dynamic['textBackground'] == 'rgba(0, 0, 0, 0)' and dynamic['shadow'] == 'none'
            for _ in range(6):
                page.evaluate(apply_script)
            assert page.locator('#ccc-wallpaper-layer').count() == 1
            assert page.locator('#ccc-wallpaper-style').count() == 1
            page.screenshot(path=str(output / f'{theme}-{width}-{"expanded" if expanded else "normal"}.png'))
            diagnostic = page.evaluate('window.__cccWallpaperV1.diagnostic()')
            assert diagnostic['nativeChrome'] == 'preserved'
            assert diagnostic['chromeStyle'] == 'single-layer' and diagnostic['nativeEdgeFades'] == 'removed'
            assert diagnostic['maskScrollHandlers'] == diagnostic['maskPositionTimers'] == 0
            page.evaluate(disable_script)
            assert page.locator('[data-ccc-wallpaper-surface]').count() == 0
            assert page.locator('#ccc-wallpaper-layer,#ccc-wallpaper-style').count() == 0
            assert page.evaluate("getComputedStyle(document.getElementById('native-footer-background')).backgroundColor") == ('rgb(255, 255, 255)' if theme == 'light' else 'rgb(24, 24, 24)')
            checks.append({'theme': theme, 'viewport': [width, height], 'scale': 1.5,
                'expanded': expanded, 'nativeComposerPixelComparison': pixel_differences,
                'singleTranslucentHeader': True, 'footerSurroundingsTransparent': True, 'nativeTopAndBottomFadesRemoved': True, 'opaqueNativeCarrierRemoved': True, 'noHardFooterEdge': True,
                'geometryAndControlsUnchanged': True, 'dynamicControlExclusion': True,
                'reapplyAndDisable': True})
        assert not errors, errors
        # Dot/ChatGPT has independent edge decorations and author-role wrappers,
        # without Codex's thread footer/fade attributes.
        dot_fixture='''<style>
        *{box-sizing:border-box}body{margin:0;font:20px/1.6 Arial}
        #root{height:100vh;background:var(--color-surface);--color-surface:rgb(255,255,255)}
        html[data-theme=dark] #root{--color-surface:rgb(24,24,24);color:white}
        main{height:100vh;background:var(--color-surface)}
        #dot-header{height:78px;position:absolute;inset:0 0 auto;background:var(--color-surface)}
        .dot-edge{position:absolute;left:0;right:0;pointer-events:none}
        #dot-top-fade{top:78px;height:40px;background:linear-gradient(var(--color-surface),transparent)}
        #dot-footer{bottom:0;height:170px;background:var(--color-surface)}
        #dot-bottom-fade{bottom:170px;height:50px;background:linear-gradient(transparent,var(--color-surface))}
        article{position:absolute;left:8%;right:15%;top:150px}
        article p{margin:20px 0;width:85%}
        #user{margin-left:35%;width:65%;background:#ef74b6;color:white;border-radius:24px;padding:12px 20px}
        #user p{margin:0;width:auto}
        #dot-card{position:absolute;left:8%;right:15%;bottom:24px;height:100px;pointer-events:auto;background:var(--color-surface);border-radius:30px;padding:16px 24px;border:1px solid #8884;box-shadow:0 2px 8px #0001}
        #interior-gradient{top:48%;height:40px;background:linear-gradient(90deg,#f006,#00f6)}
        </style><div id="root"><main>
        <div id="dot-header" data-app-shell-titlebar>Dot · 聊天</div><div id="dot-top-fade" class="dot-edge"></div>
        <article><div data-message-author-role="assistant"><p id="dot-reading">阅读底色扩大一些，保留清楚的边界，不使用泛光。</p></div>
        <div id="user" data-message-author-role="user"><p id="dot-user-text">发送消息保持原生气泡</p></div></article>
        <div id="interior-gradient" class="dot-edge"></div>
        <div id="dot-bottom-fade" class="dot-edge"></div><div id="dot-footer" class="dot-edge">
        <div id="dot-card"><div contenteditable="true"><p>发送消息</p></div></div></div>
        </main></div>'''
        for theme,width,height in [('light',2560,1440),('dark',2560,1440),('light',900,980)]:
            page.set_viewport_size({'width':width,'height':height});page.set_content(dot_fixture)
            page.evaluate('theme=>document.documentElement.dataset.theme=theme',theme)
            original=page.evaluate(style_expression,'#user,#dot-user-text,#dot-card,#dot-card *')
            page.evaluate(apply_script);page.wait_for_function("window.__cccWallpaperV1?.diagnostic().state==='applied'")
            assert page.evaluate(style_expression,'#user,#dot-user-text,#dot-card,#dot-card *')==original
            paint=page.evaluate('''()=>{
                const styles=id=>{let e=document.getElementById(id);if(id==='dot-reading')e=e.parentElement;const s=getComputedStyle(e,id==='dot-reading'?'::before':null);return {background:s.backgroundColor,image:s.backgroundImage,shadow:s.boxShadow,outline:s.outlineWidth,outlineStyle:s.outlineStyle,left:s.left}};
                return {reading:styles('dot-reading'),user:styles('dot-user-text'),edges:['dot-top-fade','dot-bottom-fade','dot-footer'].map(styles),interior:styles('interior-gradient'),diagnostic:window.__cccWallpaperV1.diagnostic()};}''')
            assert paint['reading']['shadow']=='none' and paint['reading']['outlineStyle']=='none' and paint['reading']['left']=='-10px'
            assert paint['user']['shadow']=='none' and paint['user']['outlineStyle']=='none' and paint['user']['background']=='rgba(0, 0, 0, 0)',paint
            assert all(s['background']=='rgba(0, 0, 0, 0)' and s['image']=='none' for s in paint['edges']),paint
            assert paint['interior']['image'].startswith('linear-gradient'),paint
            assert paint['diagnostic']['edges']>=3 and paint['diagnostic']['maskGlow'] is False
            page.evaluate('''()=>{const p=document.createElement('p');p.textContent='新发送的消息';document.getElementById('user').append(p);
                if(getComputedStyle(p).backgroundColor!=='rgba(0, 0, 0, 0)'||getComputedStyle(p).outlineStyle!=='none')throw Error('New outgoing message was decorated');}''')
            page.screenshot(path=str(output/f'dot-{theme}-{width}.png'))
            # Reapplying and restoring must remove every managed edge attribute.
            for _ in range(4):page.evaluate(apply_script)
            page.evaluate(disable_script)
            assert page.locator('[data-ccc-wallpaper-edge]').count()==0
            assert page.evaluate("getComputedStyle(document.getElementById('dot-top-fade')).backgroundImage.startsWith('linear-gradient')")
            checks.append({'dotMode':True,'theme':theme,'viewport':[width,height],'scale':1.5,'nativeOutgoingBubble':True,'streamedOutgoingExcluded':True,'flatExpandedReadingSurface':True,'independentEdgeLayersRemoved':True,'interiorGradientPreserved':True,'disableRestoresOriginal':True})
        # Brightness affects the image substrate instead of mixing in white.
        page.set_viewport_size({'width':900,'height':980})
        page.set_content('<style>body{margin:0}#root,main{height:100vh;background:white}</style><div id="root"><main></main></div>')
        match=re.search(r'const p=(\{[^\n]+\}), key=',apply_script);assert match
        payload=json.loads(match[1])
        def dim_script(value):
            changed={**payload,'darkness':value,'overlay':0}
            return apply_script[:match.start(1)]+json.dumps(changed,separators=(',',':'))+apply_script[match.end(1):]
        images=[]
        for darkness in [0,.5,1]:
            page.evaluate(dim_script(darkness));page.wait_for_function("window.__cccWallpaperV1?.diagnostic().state==='applied'")
            images.append(Image.open(io.BytesIO(page.screenshot())).convert('RGB'))
            assert page.evaluate("getComputedStyle(document.querySelector('#ccc-wallpaper-layer img')).opacity")=='1'
        for x,y in [(100,150),(700,500),(1100,1200)]:
            full,half,black=[im.getpixel((x,y)) for im in images]
            assert all(abs(h-f*.5)<=1 for h,f in zip(half,full)) and max(black)==0,(full,half,black)
        checks.append({'imageAlwaysFullOpacity':True,'dimmingPixelsProportional':True,'zeroOriginalAndMaximumBlack':True})
        assert not errors,errors
        result = {'success': True, 'checks': checks, 'pageErrors': errors,
            'mouseOrKeyboardControl': False, 'inferenceRequests': 0}
        (output / 'result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(result, ensure_ascii=False))
    finally:
        browser.close()
