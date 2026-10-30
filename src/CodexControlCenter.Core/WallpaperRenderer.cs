using System.Globalization;
using System.Text.Json;

namespace CodexControlCenter.Core;

/// <summary>Only owns its prefixed nodes and attributes; does not inspect text, app state or credentials.</summary>
public static class WallpaperRenderer
{
    public const string LayerId="ccc-wallpaper-layer";
    public static string BuildScript(WallpaperSettings settings,string? dataUri)
    {
        WallpaperLibrary.Validate(settings);
        if(settings.Enabled&&(dataUri is null||!System.Text.RegularExpressions.Regex.IsMatch(dataUri,@"\Adata:image/(png|jpeg|webp|bmp);base64,[A-Za-z0-9+/=]+\z")))
            throw new InvalidOperationException("壁纸图片格式无效，请重新导入。");
        return BuildCore(settings,settings.Enabled?dataUri:null,null);
    }
    public static string BuildFromRegisteredImage(WallpaperSettings settings,string? imageId)
    {
        WallpaperLibrary.Validate(settings);if(settings.Enabled&&!IsImageId(imageId))throw new InvalidOperationException("未选择可用壁纸图片。");return BuildCore(settings,null,settings.Enabled?imageId:null);
    }
    private static string BuildCore(WallpaperSettings settings,string? data,string? key)
    {
        var payload=JsonSerializer.Serialize(new{enabled=settings.Enabled,image=data,imageKey=key,
            fit=settings.Fit.Trim().ToLowerInvariant(),x=settings.PositionX,y=settings.PositionY,zoom=settings.Zoom,opacity=1,darkness=settings.Dimming,blur=settings.Blur,overlay=settings.Overlay});
        return Template.Replace("__WALLPAPER_PAYLOAD__",payload,StringComparison.Ordinal);
    }
    private static bool IsImageId(string? id)=>id is{Length:64}&&id.All(c=>c is >= 'a' and <= 'f' or >= '0' and <= '9');
    public static string RegisterImage(string id,string dataUri)
    {
        if(!IsImageId(id)||!System.Text.RegularExpressions.Regex.IsMatch(dataUri,@"\Adata:image/(png|jpeg|webp|bmp);base64,[A-Za-z0-9+/=]+\z"))throw new InvalidOperationException("壁纸图片格式或标识无效。");
        var json=JsonSerializer.Serialize(new{id,data=dataUri},new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        return "(()=>{if(window.top!==window||location.protocol!=='app:'||location.hostname!=='-'||location.pathname!=='/index.html')return false;const p="+json+";const store=window.__cccWallpaperImagesV1??=new Map();if(!store.has(p.id)){const image=new Image();image.alt='';image.draggable=false;image.src=p.data;store.set(p.id,image);}return true;})()";
    }
    public static string PruneImages(IEnumerable<string> retained)=>"(()=>{const retained=new Set("+JsonSerializer.Serialize(retained)+");const store=window.__cccWallpaperImagesV1;if(store)for(const key of store.keys())if(!retained.has(key))store.delete(key);return [...(store?.keys()??[])];})()";

    public static string BuildTrialScript(string draftScript,string committedScript,DateTimeOffset deadline)
    {
        return "(()=>{const observed="+draftScript+";if(location.protocol!=='app:'||location.hostname!=='-'||location.pathname!=='/index.html'||window.top!==window)return observed;"+
            "let timer=0;const trial={cancel(){clearTimeout(timer);if(window.__cccWallpaperTrialV1===trial)delete window.__cccWallpaperTrialV1;},renew(deadline){clearTimeout(timer);const remaining=Math.min(10000,Math.max(0,deadline-Date.now()));timer=setTimeout(()=>{if(window.__cccWallpaperTrialV1!==trial)return;trial.cancel();"+committedScript+";},remaining);}};"+
            "window.__cccWallpaperTrialV1=trial;trial.renew("+deadline.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)+");return observed;})()";
    }

    private const string Template="""
    (()=>{
      'use strict';
      if(window.top!==window||location.protocol!=='app:'||location.hostname!=='-'||location.pathname!=='/index.html')return {state:'unsupported',version:1};
      window.__cccWallpaperTrialV1?.cancel();
      const p=__WALLPAPER_PAYLOAD__, key='__cccWallpaperV1';
      const previous=window[key];
      const cleanup=()=>{
        document.querySelectorAll('#ccc-wallpaper-style,#ccc-wallpaper-layer').forEach(e=>e.remove());
        document.querySelectorAll('[data-ccc-wallpaper-surface]').forEach(e=>e.removeAttribute('data-ccc-wallpaper-surface'));
        document.querySelectorAll('[data-ccc-wallpaper-before],[data-ccc-wallpaper-after]').forEach(e=>{e.removeAttribute('data-ccc-wallpaper-before');e.removeAttribute('data-ccc-wallpaper-after');});
        document.querySelectorAll('[data-ccc-wallpaper-edge]').forEach(e=>e.removeAttribute('data-ccc-wallpaper-edge'));
        document.documentElement.removeAttribute('data-ccc-wallpaper');
        delete window[key];
      };
      if(previous&&typeof previous.dispose==='function'){try{previous.dispose();}catch{cleanup();}}
      else cleanup();
      if(!p.enabled){cleanup();return {state:'disabled',version:1};}
      const root=document.getElementById('root');
      if(!root)return {state:'loading',version:1};
      // App chrome is a native surface, including an expanded composer. Match semantic
      // attributes, never hashed application classes or the size of the controls.
      const nativeChrome=':where(header,footer,form,[data-app-shell-titlebar],[data-app-shell-main-titlebar],[data-thread-scroll-footer],[data-codex-composer-root],[data-composer-surface-variant],[contenteditable],[data-ccc-wallpaper-exclude])';
      const userMessage=':where([data-user-message-bubble],[data-message-author-role="user"],[data-conversation-role="user"],[data-turn="user"],[data-testid="user-message"],.rich-text-user-turn,.bg-user-message)';
      const readingRoot=':where([data-markdown-text-style="assistant-message"],[data-message-author-role="assistant"] [data-markdown-copy],[data-conversation-role="assistant"] [data-markdown-copy],[data-message-author-role="assistant"]:not(:has([data-markdown-text-style="assistant-message"],[data-markdown-copy])))';
      const activityRows=':is([class~="group/activity-header"],.text-size-chat.text-secondary:has(>button.text-tertiary.text-size-chat))';
      const activityExcluded=':where('+nativeChrome+','+userMessage+','+readingRoot+',aside,nav,pre,code,.turn-action-controls,[role="menu"],[role="dialog"],[role="tooltip"],[role="menubar"],[data-slot^="thread-summary-panel"])';
      const activitySelector='#root '+activityRows+':not('+activityExcluded+','+activityExcluded+' *):not([disabled],[aria-disabled="true"],[data-disabled]):not(:has(>button[disabled],>button[aria-disabled="true"]))';
      const surfaceSelector=':not('+nativeChrome+','+nativeChrome+' *)';
      const style=document.createElement('style');style.id='ccc-wallpaper-style';
      style.textContent=`
        html[data-ccc-wallpaper] #root {isolation:isolate;--ccc-wallpaper-base:Canvas;}
        #ccc-wallpaper-layer {position:fixed;inset:0;z-index:-1;pointer-events:none!important;overflow:hidden;background:var(--ccc-wallpaper-base,Canvas);filter:brightness(${1-p.darkness});}
        #ccc-wallpaper-layer img {position:absolute;max-width:none!important;max-height:none!important;opacity:1;filter:blur(${p.blur}px);pointer-events:none!important;}
        html[data-ccc-wallpaper] [data-ccc-wallpaper-surface]${surfaceSelector} {background-color:transparent!important;background-image:none!important;}
        html[data-ccc-wallpaper] [data-ccc-wallpaper-before]${surfaceSelector}::before,
        html[data-ccc-wallpaper] [data-ccc-wallpaper-after]${surfaceSelector}::after {background-color:transparent!important;background-image:none!important;}
        /* Keep native controls and one flat translucent header. Remove the separate
           native scroll-edge fades as well as the footer's decorative white board. */
        html[data-ccc-wallpaper] #root [data-app-shell-titlebar] {
          background-color:color-mix(in srgb,var(--color-surface,var(--ccc-wallpaper-base,Canvas)) 52%,transparent);
          background-image:none;
          backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);
        }
        html[data-ccc-wallpaper] #root [data-thread-scroll-footer] {
          background-color:transparent!important;
          background-image:none!important;
        }
        html[data-ccc-wallpaper] #root [data-app-shell-titlebar]::before,
        html[data-ccc-wallpaper] #root [data-app-shell-titlebar]::after,
        html[data-ccc-wallpaper] #root [data-app-shell-main-content-top-fade] > [class*="_MainContentTopFade_"],
        html[data-ccc-wallpaper] #root .thread-scroll-container .pointer-events-none.sticky > :where(.from-surface,.from-surface-secondary).pointer-events-none.absolute,
        html[data-ccc-wallpaper] #root [data-thread-scroll-footer] > :where(.pointer-events-none.absolute.bg-surface,.pointer-events-none.absolute.bg-surface-secondary),
        html[data-ccc-wallpaper] #root [data-thread-scroll-footer]::before,
        html[data-ccc-wallpaper] #root [data-thread-scroll-footer]::after {
          background-color:transparent!important;background-image:none!important;
        }
        html[data-ccc-wallpaper] #root [data-ccc-wallpaper-edge] {background-color:transparent!important;background-image:none!important;}
        @media(prefers-reduced-motion:reduce){#ccc-wallpaper-layer{transition:none!important;}}
      `;
      // Tool/subagent summaries sit directly on the wallpaper. Keep their native
      // transparent hit areas and native weight. Fix the actual leaf glyphs;
      // native important utility/shimmer colors live in cascade layers, so an
      // unlayered important rule cannot override them. Use the existing earliest
      // theme layer, without modifying any native stylesheet.
      // shadows, added background, measurements or text inspection.
      style.append(document.createTextNode(`@layer theme {
        html[data-ccc-wallpaper] ${activitySelector} {
          --agent-activity-summary-color:#111!important;
        }
        html[data-ccc-wallpaper] ${activitySelector},
        html[data-ccc-wallpaper] ${activitySelector} :where(span,a,button):not([disabled],[aria-disabled="true"],[data-disabled]) {
          color:#111!important;
          -webkit-text-fill-color:#111!important;
          text-shadow:none!important;
        }
        html[data-ccc-wallpaper] ${activitySelector} svg {
          color:#111!important;
          filter:none!important;
        }
        html[data-ccc-wallpaper] ${activitySelector} [class*="_cadencedShimmerSweep_"] {visibility:hidden!important;}
      }`));
      const layer=document.createElement('div');layer.id='ccc-wallpaper-layer';layer.setAttribute('aria-hidden','true');layer.inert=true;
      const img=p.imageKey?window.__cccWallpaperImagesV1?.get(p.imageKey):new Image();
      if(!img)return {state:'image_missing',version:1};
      img.alt='';img.draggable=false;if(!p.imageKey)img.src=p.image;layer.append(img);
      document.head.append(style);root.prepend(layer);document.documentElement.setAttribute('data-ccc-wallpaper','1');
      // Native backgrounds travel with their reading blocks in the browser's scrolling tree.
      // No viewport canvas, text measurement, scroll handler or delayed position correction.
      // A Markdown response owns exactly one plate. Tight lists cannot composite
      // translucent row outlines over each other. Fallback content groups lists
      // and quotations as a whole, and excludes every nested reading owner.
      const readingBlocks=':where(p,h1,h2,h3,h4,h5,h6,ul,ol,dl,blockquote)';
      const readingExcluded=':where('+nativeChrome+','+userMessage+','+activityRows+',#ccc-wallpaper-layer,pre,code,nav,aside,button,[role="navigation"],[role="dialog"],[role="menu"],[role="tooltip"],[aria-hidden="true"])';
      const nestedReading=':where('+readingRoot+' *,ul *,ol *,dl *,blockquote *)';
      const readingOwners=':is('+readingRoot+':not('+readingRoot+' *),'+readingBlocks+':not('+nestedReading+')):not('+readingExcluded+','+readingExcluded+' *)';
      const readingSelector='#root '+readingOwners;
      if(p.overlay>0)style.append(document.createTextNode(`
        html[data-ccc-wallpaper] ${readingSelector} {
          position:relative;isolation:isolate;
        }
        html[data-ccc-wallpaper] ${readingSelector}::before {
          content:'';position:absolute;inset:-6px -10px;z-index:-1;pointer-events:none;
          background-color:color-mix(in srgb,var(--ccc-wallpaper-base,Canvas) ${p.overlay*78}%,transparent)!important;
          border-radius:8px;box-shadow:none;outline:none;
        }
      `));
      const arrange=()=>{
        if(!img.naturalWidth||!img.naturalHeight)return;
        const w=layer.clientWidth,h=layer.clientHeight;if(!w||!h)return;
        let scale=(p.fit==='contain'?Math.min(w/img.naturalWidth,h/img.naturalHeight):Math.max(w/img.naturalWidth,h/img.naturalHeight))*p.zoom;
        if(p.blur>0&&p.fit==='cover')scale*=1+p.blur*4/675;
        const iw=img.naturalWidth*scale,ih=img.naturalHeight*scale;
        img.style.width=iw+'px';img.style.height=ih+'px';img.style.left=((w-iw)*p.x/100)+'px';img.style.top=((h-ih)*p.y/100)+'px';img.style.filter='blur('+(p.blur*Math.min(w,h)/675)+'px)';
      };
      if(!img.complete||!img.naturalWidth)img.addEventListener('load',arrange,{once:true});
      let timer=0,disposed=false,surfaces=0,edges=0,obscured=false,needsScan=true,lastScan=0,scanMs=0,scanned=0,lastSize='';
      const scan=()=>{
        if(timer){clearTimeout(timer);timer=0;}if(disposed)return;arrange();
        const size=innerWidth+'x'+innerHeight;
        if(!needsScan&&size===lastSize&&Date.now()-lastScan<5000)return;
        const scanStart=performance.now();needsScan=false;lastSize=size;lastScan=Date.now();
        const minimum=Math.max(1,innerWidth*innerHeight*.25);surfaces=0;edges=0;obscured=false;
        let examined=0,chosenBase=null;
        for(const e of [root,...root.querySelectorAll('div,main,section,aside')]){
          if(++examined>3000)break;
          if(e===layer||e.closest(nativeChrome+','+userMessage+','+readingRoot+',#ccc-wallpaper-layer,[role="dialog"],[role="menu"],[role="tooltip"],pre,code')){
            if(e!==layer){e.removeAttribute('data-ccc-wallpaper-surface');e.removeAttribute('data-ccc-wallpaper-before');e.removeAttribute('data-ccc-wallpaper-after');}continue;
          }
          if(e.clientHeight<64||e.clientWidth<64){e.removeAttribute('data-ccc-wallpaper-surface');e.removeAttribute('data-ccc-wallpaper-before');e.removeAttribute('data-ccc-wallpaper-after');continue;}
          const rect=e.getBoundingClientRect();
          if(rect.width*rect.height<minimum||rect.height<innerHeight*.60){e.removeAttribute('data-ccc-wallpaper-surface');e.removeAttribute('data-ccc-wallpaper-before');e.removeAttribute('data-ccc-wallpaper-after');continue;}
          const paint=getComputedStyle(e),bg=paint.backgroundColor;
          if(!chosenBase&&paint.visibility!=='hidden'&&(e.hasAttribute('data-ccc-wallpaper-surface')||(bg!=='rgba(0, 0, 0, 0)'&&bg!=='transparent')||paint.backgroundImage!=='none')){
            const token=paint.getPropertyValue('--color-surface').trim();
            if(token&&CSS.supports('color',token))chosenBase=token;
            else if(bg!=='rgba(0, 0, 0, 0)'&&bg!=='transparent')chosenBase=bg;
            else if(e.hasAttribute('data-ccc-wallpaper-surface')){
              e.removeAttribute('data-ccc-wallpaper-surface');const nativeBg=getComputedStyle(e).backgroundColor;e.setAttribute('data-ccc-wallpaper-surface','1');
              if(nativeBg!=='rgba(0, 0, 0, 0)'&&nativeBg!=='transparent')chosenBase=nativeBg;
            }
          }
          if(e.hasAttribute('data-ccc-wallpaper-surface')||(bg!=='rgba(0, 0, 0, 0)'&&bg!=='transparent')||paint.backgroundImage!=='none'){if(!e.hasAttribute('data-ccc-wallpaper-surface'))e.setAttribute('data-ccc-wallpaper-surface','1');surfaces++;}
          for(const side of ['before','after']){
            const pseudo=getComputedStyle(e,'::'+side);
            if(pseudo.content!=='none'&&pseudo.content!=='normal'&&parseFloat(pseudo.width)>=innerWidth*.6&&parseFloat(pseudo.height)>=innerHeight*.6&&(pseudo.backgroundImage!=='none'||(pseudo.backgroundColor!=='rgba(0, 0, 0, 0)'&&pseudo.backgroundColor!=='transparent')))e.setAttribute('data-ccc-wallpaper-'+side,'1');
          }
        }
        // Dot/ChatGPT and Codex use different scroll-edge components. Recognize
        // only wide, non-interactive decorations near the viewport edges; never
        // remove the input card, message bubbles, menus, or a content gradient.
        root.querySelectorAll('[data-ccc-wallpaper-edge]').forEach(e=>e.removeAttribute('data-ccc-wallpaper-edge'));
        let edgeExamined=0;
        for(const e of root.querySelectorAll('div')){
          if(++edgeExamined>3000)break;
          if(e.closest(userMessage+',#ccc-wallpaper-layer,aside,nav,pre,code,[contenteditable],[role="dialog"],[role="menu"],[role="tooltip"],[role="progressbar"],[data-ccc-wallpaper-exclude]'))continue;
          const r=e.getBoundingClientRect();
          if(r.width<innerWidth*.35||r.height<=0||r.height>Math.min(260,innerHeight*.32)||r.bottom<0||r.top>innerHeight)continue;
          if(r.top>innerHeight*.2&&r.bottom<innerHeight*.72)continue;
          const paint=getComputedStyle(e);
          if(paint.pointerEvents!=='none'||!['absolute','fixed','sticky'].includes(paint.position))continue;
          if(paint.backgroundImage==='none'&&(paint.backgroundColor==='rgba(0, 0, 0, 0)'||paint.backgroundColor==='transparent')&&!e.hasAttribute('data-ccc-wallpaper-edge'))continue;
          e.setAttribute('data-ccc-wallpaper-edge','1');edges++;
        }
        if(chosenBase)style.sheet.cssRules[0].style.setProperty('--ccc-wallpaper-base',chosenBase);
        for(const e of root.querySelectorAll('canvas,iframe,video,img,[role="dialog"]')){
          if(e.closest('#ccc-wallpaper-layer'))continue;
          const rect=e.getBoundingClientRect(),paint=getComputedStyle(e);
          if(rect.width*rect.height>=innerWidth*innerHeight*.90&&paint.visibility!=='hidden'&&parseFloat(paint.opacity)>.05){obscured=true;break;}
        }
        scanned=examined;scanMs=performance.now()-scanStart;
      };
      const schedule=()=>{needsScan=true;if(!timer&&!disposed)timer=setTimeout(scan,Math.max(160,750-(Date.now()-lastScan)));};
      const observer=new MutationObserver(records=>{if(records.some(r=>r.target!==layer&&!layer.contains(r.target)))schedule();});
      observer.observe(root,{childList:true,characterData:true,attributes:true,attributeFilter:['class','style','hidden','open'],subtree:true});
      const themeObserver=new MutationObserver(schedule);themeObserver.observe(document.documentElement,{attributes:true,attributeFilter:['class','style','data-theme']});themeObserver.observe(root,{attributes:true,attributeFilter:['class','style','data-theme']});
      window.addEventListener('resize',schedule,{passive:true});
      const dispose=()=>{disposed=true;clearTimeout(timer);img.removeEventListener('load',arrange);observer.disconnect();themeObserver.disconnect();window.removeEventListener('resize',schedule);cleanup();};
      const diagnostic=()=>{scan();return {state:!layer.isConnected||!style.isConnected||layer.parentElement!==document.getElementById('root')?'detached':obscured?'page_overlay':img.complete&&img.naturalWidth>0?'applied':img.complete?'image_error':'loading',version:1,surfaces,edges,scanned,scanMs,nativeChrome:'preserved',chromeStyle:'single-layer',nativeEdgeFades:'removed',wallpaperEffect:'dim',darkness:p.darkness,activityContrast:'solid-dark-leaf-glyphs',activityBackground:'native',maskGlow:false,maskExpansion:6,maskHorizontalExpansion:10,userBubbles:'native',maskMode:'single-response-surface',maskScrollHandlers:0,maskPositionTimers:0,maskCanvases:0,width:img.naturalWidth,height:img.naturalHeight,clickThrough:getComputedStyle(layer).pointerEvents==='none'};};
      window[key]={dispose,version:1,diagnostic};return diagnostic();
    })()
    """;
}
