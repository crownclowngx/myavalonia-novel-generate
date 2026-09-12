"""生成面向作者的离线说明书；复用产品文档已有的安全 Markdown 渲染，不引入新依赖。

正文只有一个 Markdown 来源。截图以内嵌 PNG 保存，复制单个 HTML 即可阅读；
交互只处理目录、打印和图片放大，不连接网络、不调用模型、不写入作品。
"""
from pathlib import Path
import hashlib
import html
import re
import runpy

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / 'novel-workbench-user-manual.md'
OUTPUT = SOURCE.with_suffix('.html')

CSS = r"""
:root{--ink:#203c38;--muted:#61726b;--paper:#fbfaf6;--line:#dce3dc;--accent:#176a59;--tint:#eaf2ec;--gold:#b88a3f}
*{box-sizing:border-box}html{scroll-behavior:auto;scroll-padding-top:90px}body{margin:0;background:var(--paper);color:var(--ink);font:16px/1.9 'Segoe UI','Microsoft YaHei',sans-serif}a{color:var(--accent);text-underline-offset:4px}button,summary{font:inherit}button{cursor:pointer}button:focus-visible,a:focus-visible,summary:focus-visible{outline:3px solid var(--gold);outline-offset:4px}.skip{position:fixed;top:-100px;background:white;padding:12px;z-index:20}.skip:focus{top:8px}
header{height:72px;border-bottom:1px solid var(--line);background:#fbfaf6f5;position:sticky;top:0;z-index:5;display:flex;align-items:center;justify-content:space-between;padding:0 4vw;gap:12px;backdrop-filter:blur(10px)}.brand{display:flex;align-items:center;gap:12px;text-decoration:none;color:var(--ink);font-weight:700}.monogram{width:36px;height:36px;border-radius:10px;background:var(--accent);color:white;text-align:center;line-height:36px;font-family:Georgia,serif;font-size:24px}.topnote{font-size:12px;color:var(--muted)}.print{border:1px solid var(--line);border-radius:7px;background:white;padding:6px 14px;color:var(--ink)}.shell{display:grid;grid-template-columns:235px minmax(0,1040px);gap:48px;max-width:1440px;margin:0 auto;padding:44px 40px 80px}aside{align-self:start;position:sticky;top:108px;max-height:calc(100vh - 125px);overflow:auto}.eyebrow{letter-spacing:.14em;text-transform:uppercase;font-size:12px;color:var(--accent);font-weight:700}.toc{display:grid;gap:5px;margin:16px 0}.toc a{font-size:14px;text-decoration:none;border-radius:7px;padding:9px 10px;color:var(--muted);line-height:1.5}.toc a.active,.toc a:hover{background:var(--tint);color:var(--accent)}.toc small{display:inline-block;min-width:26px;font-size:11px;opacity:.65}.aside-note{border-top:1px solid var(--line);padding-top:18px;font-size:12px;color:var(--muted)}main{min-width:0}.hero{padding:30px 0 36px}.hero h1{font-family:Georgia,'Microsoft YaHei',serif;font-size:clamp(32px,4vw,54px);line-height:1.3;letter-spacing:-.025em;font-weight:600;margin:16px 0 20px}.hero p{max-width:710px;color:var(--muted);font-size:18px}.pill{display:inline-block;border:1px solid #cbdccf;padding:3px 10px;border-radius:20px;font-size:12px;background:var(--tint)}.routes{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:10px;margin:26px 0}.routes a{padding:16px 12px;background:white;border:1px solid var(--line);border-radius:10px;text-decoration:none;line-height:1.6}.routes span{display:block;color:var(--gold);font:22px Georgia,serif;margin-bottom:8px}.routes b{font-size:14px}.lead-note{font-size:14px;border-left:3px solid var(--gold);padding:12px 18px;background:#f4f0e6;color:var(--muted)}.doc-section{padding:44px 0 32px;border-top:1px solid var(--line);scroll-margin-top:82px}h2{font-size:29px;line-height:1.45;font-weight:650;margin:12px 0 26px}h3{font-size:20px;line-height:1.6;margin:34px 0 12px;color:var(--accent)}p{margin:14px 0}strong{font-weight:650}code{background:#edf0e9;border-radius:4px;padding:2px 5px;overflow-wrap:anywhere;font-size:.9em}ol{counter-reset:steps;list-style:none;padding:0;display:grid;gap:10px;margin:20px 0}ol li{position:relative;padding:15px 18px 15px 55px;background:white;border:1px solid var(--line);border-radius:9px}ol li:before{counter-increment:steps;content:counter(steps);position:absolute;left:16px;top:17px;border-radius:50%;width:25px;height:25px;background:var(--tint);color:var(--accent);font-size:13px;text-align:center;line-height:25px}ul{padding-left:24px}.table-wrap{max-width:100%;overflow:auto;border:1px solid var(--line);border-radius:10px;margin:24px 0;background:white}table{border-collapse:collapse;width:100%;font-size:14px;line-height:1.8}th{background:var(--tint);color:var(--accent);font-weight:650;text-align:left}td,th{padding:13px 16px;vertical-align:top;border-bottom:1px solid var(--line);min-width:130px}td:first-child{font-weight:600;width:23%}tr:last-child td{border-bottom:0}figure{margin:26px 0;background:#f0f3ee;border:1px solid var(--line);border-radius:12px;padding:14px}.capture{display:block;width:100%;padding:0;border:0;background:transparent;cursor:zoom-in}.native-capture{display:block;width:100%;height:auto;margin:auto;border-radius:5px}.portrait .capture{max-width:640px;margin:auto}figcaption{font-size:13px;line-height:1.8;color:var(--muted);padding:14px 6px 2px}.zoom-hint{display:block;font-size:11px;text-align:right;opacity:.8}.flow{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin:24px 0}.flow div{padding:17px;border-radius:10px;background:var(--accent);color:white}.flow span{font-size:12px;opacity:.7}.flow b{display:block;font-size:17px}.flow small{display:block;opacity:.85;line-height:1.65;margin-top:7px}.flow .author{background:#ecdfc6;color:#574528}.mobile-nav{display:none}footer{border-top:1px solid var(--line);padding:28px 0;color:var(--muted);font-size:12px;display:flex;justify-content:space-between;gap:15px}dialog{padding:12px;max-width:96vw;max-height:94vh;border:1px solid var(--line);border-radius:12px;background:var(--paper)}dialog::backdrop{background:#102b28c9}dialog .zoom-tools{display:flex;align-items:center;justify-content:space-between;gap:18px;font-size:13px;margin-bottom:10px}dialog img{display:block;max-width:100%;height:auto;margin:auto}dialog .zoom-scroll{overflow:auto;max-height:78vh}dialog .print{white-space:nowrap}
@media(max-width:1120px){.shell{grid-template-columns:195px minmax(0,1fr);gap:28px;padding:32px 24px}.routes{grid-template-columns:repeat(3,minmax(0,1fr))}.flow{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media(max-width:800px){header{padding:0 18px;height:65px}.topnote{display:none}.brand{font-size:14px}.shell{display:block;padding:18px}.shell aside{display:none}.mobile-nav{display:block;border-bottom:1px solid var(--line);padding:10px 18px}.mobile-nav summary{cursor:pointer;font-size:14px}.mobile-nav .toc{grid-template-columns:1fr 1fr}.hero{padding:24px 0}.hero p{font-size:16px}.routes{grid-template-columns:repeat(2,minmax(0,1fr))}.routes a:last-child{grid-column:1/-1}.doc-section{padding-top:30px}h2{font-size:25px}td,th{padding:10px;min-width:120px}.table-wrap{font-size:13px}figure{padding:7px}figcaption{padding:10px 5px}.print{font-size:12px;padding:6px 10px}footer{display:block}}
@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}}
@media print{html{scroll-behavior:auto}body{font-size:10pt;color:#172c29;background:white}header,aside,.routes,.mobile-nav,dialog,.skip,.zoom-hint{display:none!important}.shell{display:block;padding:0;max-width:none}.hero{padding:0}.hero h1{font-size:28pt}.hero p{font-size:12pt}.doc-section{padding:20px 0}h2{font-size:19pt}h3{font-size:13pt;break-after:avoid}figure,ol li,.flow{break-inside:avoid}.native-capture{max-height:155mm;width:auto;max-width:100%;object-fit:contain}table{font-size:9pt}.table-wrap{overflow:visible}td,th{min-width:0;padding:7px}.flow{grid-template-columns:repeat(4,1fr)}.flow div{background:#edf3ee;color:#173f34}.flow b{font-size:11pt}.flow small{font-size:9pt}a{color:inherit;text-decoration:none}@page{size:A4;margin:15mm}}
"""

JS = r"""
const dialog=document.getElementById('image-dialog'),zoom=document.getElementById('zoom-image');
let opener=null;
document.querySelectorAll('.capture').forEach(button=>button.addEventListener('click',()=>{
 opener=button;const original=button.querySelector('img');zoom.src=original.src;zoom.alt=original.alt;
 document.getElementById('zoom-title').textContent=original.alt;dialog.showModal();
}));
document.getElementById('close-image').addEventListener('click',()=>dialog.close());
dialog.addEventListener('click',event=>{if(event.target===dialog)dialog.close();});
dialog.addEventListener('close',()=>{if(opener)opener.focus();});
document.getElementById('print').addEventListener('click',()=>window.print());
const links=[...document.querySelectorAll('.toc a')];
const observer=new IntersectionObserver(entries=>{entries.forEach(entry=>{if(entry.isIntersecting){
 links.forEach(link=>{const active=link.hash==='#'+entry.target.id;link.classList.toggle('active',active);
 if(active)link.setAttribute('aria-current','location');else link.removeAttribute('aria-current');});
}});},{rootMargin:'-10% 0px -65% 0px'});
document.querySelectorAll('main section[id]').forEach(section=>observer.observe(section));
// 移动目录折叠会改变文档高度，先收起再定位，避免锚点按展开时的位置跳转。
document.querySelectorAll('.mobile-nav a').forEach(link=>link.addEventListener('click',event=>{
 event.preventDefault();document.querySelector('.mobile-nav').open=false;
 history.replaceState(null,'',link.hash);document.querySelector(link.hash).scrollIntoView({behavior:'instant',block:'start'});
}));
"""

FLOW = '''<div class="flow" aria-label="准备、生成审校、工作稿、人工定稿四阶段流程">
<div><span>01 / 准备</span><b>规范与章纲</b><small>背景、文风、实体<br>确认前文与本次预算</small></div>
<div><span>02 / 创作</span><b>生成与审校</b><small>正文 → 检查 → 有限修正<br>有阻塞问题时停止</small></div>
<div><span>03 / 保存</span><b>通过的工作稿</b><small>正文、摘要、事实一起保存<br>连续运行进入下一章</small></div>
<div class="author"><span>04 / 作者</span><b>审阅与定稿</b><small>比较、改稿、复核<br>明确定稿后导出</small></div></div>'''


def build():
    # 复用现有受限 Markdown 语法；run_name 不为 __main__，不会重建产品需求页。
    render = runpy.run_path(str(ROOT / 'render-product-html.py'))['render_blocks']
    source = SOURCE.read_text(encoding='utf-8-sig')
    chunks = re.split(r'(?m)^## (.+)\n', source)
    sections, nav = [], []
    for i in range(1, len(chunks), 2):
        number = (i - 1) // 2
        title, body = chunks[i].strip(), render(chunks[i + 1].splitlines())
        label = re.sub(r'^\d+\.\s*', '', title)
        nav.append(f'<a href="#guide-{number}"><small>{number:02d}</small>{html.escape(label)}</a>')
        # 图片与紧随其后的说明合成 figure；原图保持不变，标注置于图外以免遮挡界面。
        def figure(match):
            portrait = 'material-calibration' in match[1] or '材料校准' in match[1]
            return ('<figure' + (' class="portrait"' if portrait else '') + '><button type="button" class="capture" aria-label="放大界面截图">'
                    + match[1] + '</button><figcaption>' + match[2]
                    + '<span class="zoom-hint">点击图片放大 · Esc 返回</span></figcaption></figure>')
        body = re.sub(r'<p>(<img[^>]+>)</p>\s*<p>(.*?)</p>', figure, body, flags=re.S)
        if number == 6:
            body = FLOW + body
        sections.append(f'<section class="doc-section" id="guide-{number}" aria-labelledby="heading-{number}"><span class="eyebrow">阅读路线 / {number:02d}</span><h2 id="heading-{number}">{html.escape(title)}</h2>{body}</section>')
    toc = '<nav class="toc" aria-label="说明书目录">' + ''.join(nav) + '</nav>'
    routes = ''.join(f'<a href="#guide-{n}"><span>0{n-1}</span><b>{label}</b></a>' for n, label in [(2, '从空白写新书'), (3, '提炼参考文风'), (4, '接入旧稿续写'), (5, '手工设置场景'), (6, '生成与调优')])
    intro = render(chunks[0].splitlines()[1:])
    digest = hashlib.sha256(source.encode('utf-8')).hexdigest()
    output = f'''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><meta name="source-sha256" content="{digest}"><meta name="description" content="面向普通作者的小说创作工作台使用说明：新书、文风提炼、旧稿续写、手工设定、生成调优，含六张实际界面截图。"><title>网络小说创作工作台 · 用户说明书</title><style>{CSS}</style></head>
<body><a class="skip" href="#main">跳转到说明书正文</a><header><a class="brand" href="#start"><span class="monogram" aria-hidden="true">N</span>网络小说创作工作台<span class="topnote">用户说明书</span></a><button class="print" id="print" type="button">打印 / 保存 PDF</button></header>
<details class="mobile-nav"><summary>打开阅读目录 · 选择使用场景</summary>{toc}</details>
<div class="shell"><aside><div class="eyebrow">作者使用指南 / V1.0</div>{toc}<div class="aside-note">6 张实际界面截图<br>5 条使用路线<br>支持离线阅读与打印<br><br>适用：2026.09.12 工程候选<br>截图为界面验证演示数据</div></aside><main id="main">
<section class="hero" id="start"><span class="pill">从第一章开始，逐步完成你的故事</span><h1>把想写的故事，<br>一步步写出来。</h1><p>从一个空白创意，到接着已有章节往下写。这份说明书告诉你：在哪里填写、按什么顺序操作、看到什么结果才算完成。</p><div class="routes">{routes}</div><div class="lead-note">{intro}</div></section>
{''.join(sections)}<footer><span>网络小说创作工作台 · 用户说明书 v1.0 · 2026-09-12</span><a href="#start">回到开始 ↑</a></footer></main></div>
<dialog id="image-dialog" aria-labelledby="zoom-title"><div class="zoom-tools"><span id="zoom-title">界面截图</span><button class="print" id="close-image" type="button">关闭 / Esc</button></div><div class="zoom-scroll"><img id="zoom-image" alt=""></div></dialog><script>{JS}</script></body></html>
'''
    OUTPUT.write_text(output, encoding='utf-8')
    print(f'User manual generated: {len(sections)} sections, {output.count("class=\"capture\"")} captures, {OUTPUT.stat().st_size} bytes')


if __name__ == '__main__':
    build()
