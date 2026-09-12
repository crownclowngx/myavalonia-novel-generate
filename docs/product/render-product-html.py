"""Build the self-contained product document. Python standard library only.

Run: python docs/product/render-product-html.py
The renderer supports the Markdown constructs used by the accompanying PRD.
"""

from pathlib import Path
import hashlib
import base64
import html
import re

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "novel-generation-product-requirements.md"
OUTPUT = SOURCE.with_suffix(".html")

CSS = r"""
.native-capture{display:block;width:100%;height:auto;border:1px solid #d9ded6;border-radius:8px;margin:18px 0;}
:root{--paper:#f7f8f5;--white:#fff;--ink:#253731;--muted:#626e67;--line:#dce3dc;--green:#21664f;--wash:#e8f1e9;--amber:#896024;--amber-wash:#faf1df;--serif:'Noto Serif SC','Source Han Serif SC','Songti SC','SimSun',serif;--sans:Inter,'Segoe UI','Microsoft YaHei',sans-serif}
*{box-sizing:border-box}html{scroll-behavior:smooth;scroll-padding-top:88px}body{margin:0;background:var(--paper);color:var(--ink);font:15px/1.85 var(--sans)}button,a,input{ -webkit-tap-highlight-color:transparent}button{font:inherit;cursor:pointer}a{color:var(--green);text-underline-offset:4px}button:focus-visible,a:focus-visible{outline:3px solid #b88434;outline-offset:4px}button:disabled{cursor:default}::selection{background:#d1e4d5}p{margin:14px 0}h1,h2,h3{line-height:1.5}h2{font-size:26px;letter-spacing:-.5px;margin:0 0 26px}h3{font-size:18px;margin:30px 0 14px}code{font:13px/1.6 Consolas,monospace;background:#edf0ea;padding:3px 5px;border-radius:4px;overflow-wrap:anywhere}strong{font-weight:650}ol,ul{padding-left:24px}li{padding-left:4px;margin:10px 0}small{font-size:12px}.skip{position:absolute;left:16px;top:-100px;z-index:10}.skip:focus{top:15px;background:#fff;padding:10px}.topbar{position:sticky;top:0;z-index:8;height:68px;padding:0 32px;display:flex;align-items:center;justify-content:space-between;gap:20px;border-bottom:1px solid var(--line);background:#fcfdfb}.brand{display:flex;align-items:center;gap:12px;text-decoration:none;color:var(--ink)}.brand-mark{background:var(--green);color:#fff;width:32px;height:36px;display:grid;place-items:center;font:24px var(--serif);border-radius:3px 8px 8px 3px}.brand strong{font-size:14px;letter-spacing:1px}.brand small{font-size:10px;color:var(--muted);letter-spacing:1.7px;display:block;line-height:1.4}.top-actions{display:flex;align-items:center;gap:20px;font-size:12px}.plain-button{border:1px solid var(--line);background:white;color:var(--ink);border-radius:5px;padding:7px 13px}.status-chip{font-size:11px;color:var(--amber);background:var(--amber-wash);border:1px solid #e9d8b3;border-radius:4px;padding:3px 8px;white-space:nowrap}.layout{max-width:1570px;margin:auto;display:grid;grid-template-columns:245px minmax(0,1fr)}.sidebar{padding:34px 20px 24px 30px;position:sticky;top:68px;height:calc(100vh - 68px);overflow:auto;border-right:1px solid var(--line);display:flex;flex-direction:column;gap:24px}.nav-kicker,.eyebrow{font-size:11px;font-weight:600;letter-spacing:1.8px;text-transform:uppercase;color:var(--muted)}.nav-kicker{margin:0 0 12px}.toc{display:grid;gap:3px}.toc a{text-decoration:none;color:var(--muted);padding:7px 10px;border-left:2px solid transparent;font-size:12px;line-height:1.6}.toc a span{font:11px Consolas,monospace;color:#7f8e83;margin-right:9px}.toc a:hover{background:#edf1eb}.toc a.active{background:var(--wash);border-left-color:var(--green);color:var(--green)}.side-note{margin-top:auto;padding:17px 10px 0;border-top:1px solid var(--line);font-size:11px;color:var(--muted)}.reading-line{height:3px;background:#e3e9e0;margin:14px 0 7px}.reading-line span{display:block;height:100%;background:var(--green);width:0}.content{min-width:0;padding:0 54px 50px}.hero{padding:58px 0 40px;border-bottom:1px solid var(--line)}.hero-grid{display:grid;grid-template-columns:minmax(0,1fr) 230px;gap:35px;align-items:center}.eyebrow{color:var(--green);display:flex;align-items:center;gap:12px}.eyebrow:before{content:'';width:24px;height:1px;background:var(--green)}h1{font:500 clamp(33px,3.6vw,53px)/1.45 var(--serif);letter-spacing:1px;margin:20px 0}.hero-lead{color:var(--muted);font-size:15px;max-width:600px}.hero-flow{padding:25px 0 25px 28px;border-left:1px solid var(--line)}.hero-flow-item{display:flex;gap:16px;align-items:center}.hero-flow-item>span{font:italic 25px var(--serif);color:#a3afa3}.hero-flow-item b{display:block;font-size:14px;font-weight:600}.hero-flow-item small{color:var(--muted);font-size:11px}.hero-flow .down{color:#91a591;margin:7px 0 7px 44px}.meta-strip{display:flex;gap:24px;flex-wrap:wrap;margin-top:30px;padding-top:23px;border-top:1px solid var(--line);font-size:11px;color:var(--muted)}.meta-strip b{font-size:20px;color:var(--ink);font-weight:500;margin-right:7px}.hero-caption{margin:24px 0 0;display:flex;gap:12px;align-items:center;color:var(--muted);font-size:12px}.intro{margin-top:28px;padding:23px 26px;background:#eef2ea;border-left:3px solid #869e79;font-size:13px}.intro p:first-child{margin-top:0}.intro p:last-child{margin-bottom:0}.intro p:first-child{font-size:12px;color:var(--muted)}.doc-section{padding-top:52px;margin-top:4px}.doc-section+.doc-section{border-top:1px solid var(--line);margin-top:45px}.section-count{font-size:10px;letter-spacing:2px;color:var(--green);display:block;margin-bottom:9px}.table-wrap{width:100%;overflow-x:auto;margin:24px 0;border:1px solid var(--line);border-radius:7px;background:#fff}table{border-collapse:collapse;width:100%;font-size:13px;line-height:1.8}th{text-align:left;font-size:12px;font-weight:650;background:#edf2eb;color:#385343;white-space:normal}th,td{padding:14px 17px;border-bottom:1px solid #e4e9e2;vertical-align:top}td:first-child{font-weight:550;min-width:100px}tr:last-child td{border-bottom:0}tbody tr:nth-child(even){background:#fbfcf9}td a{overflow-wrap:anywhere}.requirement{padding:24px 28px;border:1px solid var(--line);border-radius:7px;margin:20px 0;background:#fff;position:relative}.requirement h3{margin:0 0 16px;font-size:17px;padding-right:2px}.requirement .req-phase{display:block;color:var(--green);font-size:11px;font-weight:500;margin-top:5px;letter-spacing:.1px}.requirement p{font-size:14px}.requirement p:last-child{font-size:12px;border-top:1px solid var(--line);padding-top:14px;margin-bottom:0;color:#50664f}.requirement .table-wrap{border-radius:3px}.figure{margin:28px 0 32px}.figure-head{display:flex;align-items:start;justify-content:space-between;gap:16px;margin-bottom:18px}.figure-index{font:11px Consolas,monospace;color:var(--green);display:block;margin-bottom:4px}.figure-title{margin:0;font:500 22px/1.5 var(--serif)}.figure-desc{font-size:12px;color:var(--muted);margin:5px 0 0}.figure-note{font-size:11px;color:var(--muted);margin-top:12px}.switcher{display:flex;gap:4px;flex-wrap:wrap;padding:4px;border:1px solid var(--line);background:#edf1ea;border-radius:6px}.switcher button{border:0;background:transparent;color:#536250;padding:7px 12px;font-size:12px;border-radius:4px}.switcher button[aria-selected=true]{background:#fff;color:var(--green);box-shadow:0 1px 4px #25373115}.prototype{border:1px solid #ccd6cc;border-radius:8px;overflow:hidden;background:#fff;box-shadow:0 18px 36px #233a2010}.app-top{background:#edf1eb;border-bottom:1px solid var(--line);padding:10px 16px;display:flex;justify-content:space-between;align-items:center;font-size:11px;gap:10px}.app-top .app-name{font-weight:600}.app-top small{color:var(--muted);font-size:10px}.app-tabs{display:flex;border-bottom:1px solid var(--line);background:#f7f9f5;font-size:11px;padding:0 14px;gap:22px}.app-tabs span{padding:11px 0}.app-tabs span:first-child{border-bottom:2px solid var(--green);color:var(--green)}.workspace{display:grid;grid-template-columns:155px minmax(0,1fr) 195px;min-height:390px}.book-nav{background:#fafbf8;border-right:1px solid var(--line);padding:20px 12px}.mini-label{font-size:10px;text-transform:uppercase;letter-spacing:1px;color:var(--muted)}.book-nav h4{font-size:12px;margin:10px 7px 20px}.chapter-button{display:block;border:0;width:100%;text-align:left;background:none;color:#586850;font-size:11px;line-height:1.8;padding:10px 8px;border-radius:4px;margin-bottom:6px}.chapter-button small{display:block;color:var(--muted);font-size:10px}.chapter-button[aria-pressed=true]{background:var(--wash);color:var(--green)}.manuscript{padding:24px 28px;min-width:0}.chapter-meta{display:flex;gap:8px;font-size:10px;color:var(--muted);align-items:center}.mini-tag{font-size:10px;background:var(--wash);color:var(--green);padding:2px 6px;border-radius:3px}.manuscript h4{font:500 24px/1.6 var(--serif);margin:24px 0 18px}.sample-copy{font:14px/2.2 var(--serif);color:#425043}.sample-copy p{margin:13px 0}.sample-copy mark{background:#f5e7be;color:inherit;padding:2px}.excerpt-note{font-size:10px;color:#71816f;margin-top:24px}.inspector{border-left:1px solid var(--line);background:#f9fbf7;padding:20px 15px}.inspector h5{font-size:11px;margin:0 0 17px}.rule-row{font-size:10px;border-bottom:1px solid var(--line);padding:9px 0;display:flex;justify-content:space-between;gap:8px}.rule-row span:last-child{color:var(--green)}.review-issue{font-size:11px;line-height:1.8;padding:12px;margin-top:20px;background:var(--amber-wash);border-left:2px solid #c39544}.review-issue b{display:block;color:var(--amber);font-size:11px;margin-bottom:6px}.review-issue.clear{background:var(--wash);border-color:var(--green)}.review-issue.clear b{color:var(--green)}.app-status{padding:9px 14px;font-size:10px;color:#597150;display:flex;align-items:center;justify-content:space-between;gap:10px;border-top:1px solid var(--line);background:#f3f7ef}.dot{display:inline-block;width:5px;height:5px;border-radius:50%;background:var(--green);margin-right:5px}.dot.amber{background:var(--amber)}.tool-content{padding:26px;min-height:437px}.tool-heading{display:flex;justify-content:space-between;gap:20px;align-items:center;margin-bottom:20px}.tool-heading h4{font-size:20px;font-family:var(--serif);font-weight:500;margin:0}.tool-heading p{font-size:12px;color:var(--muted);margin:5px 0}.display-action{background:var(--green);color:#fff;border-radius:4px;padding:7px 12px;font-size:11px;white-space:nowrap}.template-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:15px}.template-card{border:1px solid var(--line);padding:20px;border-radius:5px}.template-card.active{border-color:#82a08a;background:#f7faf4}.book-spine{height:65px;width:47px;background:#457b61;box-shadow:inset 5px 0 0 #00000017;border-radius:2px 6px 6px 2px;color:#fff;display:grid;place-items:center;font:19px var(--serif);margin-bottom:19px}.book-spine.gold{background:#a18b55}.book-spine.gray{background:#748483}.template-card h5{font-size:12px;margin:0 0 8px}.template-card p{font-size:11px;color:var(--muted);line-height:1.8;min-height:56px}.tiny-tags{display:flex;gap:5px;flex-wrap:wrap;font-size:9px;color:var(--green)}.tiny-tags span{padding:2px 5px;background:var(--wash)}.template-card footer{border-top:1px solid var(--line);padding-top:12px;margin-top:15px;font-size:10px;color:var(--muted)}.tool-bottom{font-size:11px;color:var(--muted);padding-top:19px}.connection-layout{display:grid;grid-template-columns:170px 1fr;gap:26px}.connection-choice{background:var(--wash);padding:14px;font-size:12px;border-radius:5px;color:var(--green)}.connection-choice small{display:block;font-size:10px;color:var(--muted);margin-top:7px}.connection-fields{display:grid;grid-template-columns:1fr 1fr;gap:17px}.field-label{font-size:10px;color:var(--muted);display:block;margin-bottom:6px}.field-value{padding:9px 11px;border:1px solid var(--line);border-radius:4px;font-size:12px;background:#fafbf8;overflow-wrap:anywhere}.field.full{grid-column:1/-1}.connection-note{font-size:11px;padding:13px;background:var(--wash);color:#496d48;margin-top:18px}.flow-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:24px;background:#edf2ea;padding:26px;border-radius:7px}.flow-card{position:relative;background:#fff;border-top:3px solid #72936c;padding:20px 17px;min-height:150px}.flow-card:not(:last-child):after{content:'→';position:absolute;right:-19px;top:60px;color:#76906f}.flow-card span{font:12px Consolas,monospace;color:#7f9773}.flow-card h4{font-size:14px;margin:10px 0 8px}.flow-card p{font-size:11px;line-height:1.8;color:var(--muted);margin:0}.flow-card.human{border-top-color:#b89150;background:#fffaf0}.flow-sub{display:grid;grid-template-columns:1fr 1fr;gap:20px;padding:16px 24px;border:1px solid var(--line);border-top:0;border-radius:0 0 7px 7px;font-size:12px}.flow-sub b{display:block;font-size:11px;color:var(--green)}.flow-sub>div:last-child b{color:var(--amber)}.asset-diagram{display:grid;grid-template-columns:1fr 74px 1.35fr;gap:15px;align-items:center;border:1px solid var(--line);background:#fff;border-radius:7px;padding:28px}.asset-source{border:1px solid #aec3aa;background:#f1f6ed;padding:25px 22px;border-radius:5px}.asset-source h4{margin:7px 0 15px;font-size:16px;font-family:var(--serif);font-weight:500}.asset-source p{font-size:12px;line-height:2;margin:0;color:var(--muted)}.asset-arrow{text-align:center;color:var(--green);font-size:24px}.asset-arrow small{font:10px/1.7 var(--sans);display:block;color:var(--muted)}.asset-books{display:grid;gap:14px}.asset-book{border:1px solid var(--line);border-left:3px solid #759a73;padding:14px 18px;font-size:13px}.asset-book:last-child{border-left-color:#b49963}.asset-book b{display:block;margin-bottom:5px}.asset-book small{font-size:11px;color:var(--muted)}.version-flow{display:grid;grid-template-columns:repeat(3,1fr);gap:28px;padding:26px;background:#fff;border:1px solid var(--line);border-radius:6px}.version-item{position:relative}.version-item:not(:last-child):after{content:'→';position:absolute;right:-22px;top:25px;color:#8d9b83}.version-item>small{font-size:10px;color:var(--muted)}.version-item h4{font:500 21px var(--serif);margin:9px 0 10px}.version-item p{font-size:12px;margin:0;color:var(--muted)}.version-item .state-label{font-size:10px;display:inline-block;color:var(--green);background:var(--wash);padding:2px 6px;margin-top:14px}.version-item:last-child .state-label{color:var(--amber);background:var(--amber-wash)}.roadmap{display:grid;grid-template-columns:repeat(4,1fr);margin:27px 0 32px;gap:20px}.roadmap-item{border-top:2px solid #c6d3c0;padding-top:19px}.roadmap-item.focus{border-top-color:var(--green)}.roadmap-item span{font:26px var(--serif);color:#809278}.roadmap-item.focus span{color:var(--green)}.roadmap-item h4{font-size:13px;margin:10px 0}.roadmap-item p{font-size:11px;color:var(--muted);line-height:1.9;margin:0}.roadmap-item small{font-size:10px;color:var(--green);display:block;margin-top:12px}.page-footer{margin-top:50px;padding:24px 0;border-top:1px solid var(--line);display:flex;justify-content:space-between;gap:20px;font-size:11px;color:var(--muted)}[hidden]{display:none!important}.mobile-nav{display:none}details>summary{cursor:pointer}.screen-reader{position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%)}
@media(min-width:1500px){.content{padding-left:68px;padding-right:68px}}
@media(max-width:1150px){.layout{grid-template-columns:205px minmax(0,1fr)}.sidebar{padding:26px 12px 20px 18px}.content{padding-left:30px;padding-right:30px}.hero-grid{grid-template-columns:1fr}.hero-flow{display:none}.workspace{grid-template-columns:130px minmax(0,1fr)}.inspector{grid-column:1/-1;border-left:0;border-top:1px solid var(--line);display:grid;grid-template-columns:1fr 1fr;gap:18px;padding:16px}.inspector .review-issue{margin-top:0}.inspector h5{margin-bottom:6px}.workspace{min-height:0}.manuscript{min-height:320px}.flow-grid{gap:20px;padding:20px}.flow-card{padding:15px 12px}.flow-card:not(:last-child):after{right:-17px}.figure-head{flex-direction:column}.template-card{padding:15px}.app-top{flex-wrap:wrap}}
@media(max-width:820px){.layout{display:block}.sidebar{display:none}.content{padding:0 25px 35px}.topbar{height:62px;padding:0 22px}.top-actions{gap:12px}.top-actions>a{display:none}.mobile-nav{display:block;position:sticky;top:62px;z-index:7;background:#fcfdfb;border-bottom:1px solid var(--line);padding:10px 24px;font-size:12px}.mobile-nav nav{padding:13px 0;display:grid;grid-template-columns:1fr 1fr;gap:8px}.mobile-nav nav a{color:var(--muted);text-decoration:none}.hero{padding-top:37px}.hero-grid{display:grid;grid-template-columns:minmax(0,1fr) 175px;gap:23px}.hero-flow{display:block;padding-left:20px}.hero-flow-item{gap:9px}.hero-flow-item b{font-size:12px}.hero-flow-item small{font-size:10px}.hero-flow-item>span{font-size:20px}h1{font-size:36px}.hero-lead{font-size:13px}html{scroll-padding-top:120px}.doc-section{scroll-margin-top:8px}.template-card p{min-height:0}}
@media(max-width:560px){body{font-size:14px}.topbar{padding:0 16px}.brand strong{font-size:12px}.brand small{font-size:8px}.top-actions .status-chip{display:none}.plain-button{font-size:11px;padding:6px 9px}.content{padding-left:17px;padding-right:17px}.hero-grid{grid-template-columns:1fr}.hero-flow{display:none}h1{font-size:34px}.hero{padding-top:31px}.hero-caption{align-items:start}.meta-strip{gap:15px}.meta-strip b{font-size:17px}.intro{padding:17px;font-size:12px}.intro code{font-size:11px}.doc-section{padding-top:35px}h2{font-size:22px;margin-bottom:20px}h3{font-size:16px}.requirement{padding:19px 18px}.requirement h3{font-size:16px}.requirement p{font-size:13px}.table-wrap table{min-width:570px}.figure-title{font-size:21px}.workspace{grid-template-columns:1fr}.book-nav{border-right:0;border-bottom:1px solid var(--line);padding:10px}.book-nav h4{margin:5px 0 10px}.book-nav .chapter-buttons{display:grid;grid-template-columns:repeat(3,1fr);gap:5px}.chapter-button{font-size:10px;padding:7px;margin:0}.chapter-button small{font-size:9px}.book-nav .mini-label{display:none}.manuscript{padding:22px;min-height:0}.manuscript h4{font-size:22px;margin:18px 0 12px}.sample-copy{font-size:13px}.inspector{grid-template-columns:1fr;padding:16px;gap:12px}.rule-row{padding:6px 0}.app-status{align-items:start;flex-direction:column;gap:3px}.app-tabs{gap:15px;flex-wrap:wrap}.app-top{padding:10px 12px}.tool-content{padding:19px;min-height:0}.tool-heading{align-items:start;gap:10px}.tool-heading h4{font-size:18px}.tool-heading p{font-size:11px}.display-action{font-size:10px;padding:5px 8px}.template-grid{grid-template-columns:1fr}.template-card{display:grid;grid-template-columns:48px 1fr;column-gap:14px;padding:16px}.book-spine{grid-row:1/5;margin:0}.template-card p{margin:0 0 8px;font-size:11px}.template-card footer{grid-column:2;font-size:10px}.connection-layout{grid-template-columns:1fr;gap:18px}.connection-fields{gap:12px;grid-template-columns:1fr}.field.full{grid-column:auto}.flow-grid{grid-template-columns:1fr;gap:24px;padding:20px}.flow-card{min-height:0;padding:14px 17px}.flow-card:not(:last-child):after{content:'↓';right:auto;top:auto;bottom:-25px;left:calc(50% - 6px)}.flow-card h4{display:inline;margin-left:10px;font-size:14px}.flow-card p{margin-top:7px}.flow-sub{grid-template-columns:1fr;padding:17px;gap:12px}.asset-diagram{grid-template-columns:1fr;padding:20px;gap:15px}.asset-arrow{font-size:0}.asset-arrow:before{content:'↓';font-size:22px}.asset-arrow small{font-size:10px}.asset-source{padding:20px}.version-flow{grid-template-columns:1fr;padding:22px;gap:29px}.version-item:not(:last-child):after{content:'↓';right:0;top:auto;bottom:-25px}.version-item h4{font-size:20px}.roadmap{grid-template-columns:1fr 1fr;gap:25px}.page-footer{flex-direction:column;gap:8px}.switcher{width:100%}.switcher button{flex:1;padding:7px 4px;font-size:11px}.figure-note{font-size:10px}}
@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}}
@media print{@page{size:A4;margin:16mm 13mm}html{scroll-padding-top:0}body{background:white;font-size:10pt;line-height:1.7}.topbar,.sidebar,.mobile-nav,.switcher,.top-actions,.page-footer{display:none!important}.layout{display:block}.content{padding:0}.hero{padding:0 0 20px}.hero-grid{grid-template-columns:1fr}.hero-flow{display:none}h1{font-size:30pt}.intro{font-size:9pt}.doc-section{padding-top:20px;margin-top:15px;break-before:auto}.doc-section+.doc-section{margin-top:25px}h2{font-size:17pt;break-after:avoid}h3{font-size:12pt;break-after:avoid}.requirement{break-inside:auto;padding:15px}.requirement p{font-size:9pt}.table-wrap{overflow:visible;border-radius:0}table,.table-wrap table{min-width:0;font-size:8pt;table-layout:fixed}th,td{padding:7px;overflow-wrap:anywhere;min-width:0!important}thead{display:table-header-group}tr{break-inside:avoid}.figure,.prototype,.roadmap{break-inside:avoid}.prototype{box-shadow:none}.workspace{grid-template-columns:110px minmax(0,1fr) 155px}.inspector{grid-column:auto;display:block;border-top:0;border-left:1px solid var(--line)}.inspector .review-issue{margin-top:15px}.manuscript{padding:18px;min-height:0}.sample-copy{font-size:10pt}.app-status{font-size:8pt}.flow-grid{grid-template-columns:repeat(4,1fr);padding:15px;gap:16px}.flow-card{min-height:140px;padding:14px 10px}.flow-card p{font-size:8pt}.flow-card:not(:last-child):after{right:-14px}.figure-head{display:block}.figure-desc,.figure-note{font-size:8pt}.connection-layout{grid-template-columns:150px 1fr}.template-grid{grid-template-columns:repeat(3,1fr)}.asset-diagram{grid-template-columns:1fr 60px 1.3fr}.version-flow{grid-template-columns:repeat(3,1fr)}a{color:inherit;text-decoration:none}*{-webkit-print-color-adjust:exact;print-color-adjust:exact}}
"""

WORKSPACE = r"""
<figure class="figure" id="product-preview" aria-labelledby="preview-heading">
 <div class="figure-head"><div><span class="figure-index">FIG. 01 / PRODUCT WORKSPACE</span><h3 class="figure-title" id="preview-heading">一本书，一个专注的创作空间</h3><p class="figure-desc">切换下方三个入口，查看工作区与共享工具的职责。</p></div>
 <div class="switcher" role="tablist" aria-label="产品界面示意"><button role="tab" id="tab-writing" aria-controls="panel-writing" aria-selected="true" tabindex="0">小说工作区</button><button role="tab" id="tab-templates" aria-controls="panel-templates" aria-selected="false" tabindex="-1">创作模板库</button><button role="tab" id="tab-models" aria-controls="panel-models" aria-selected="false" tabindex="-1">模型与连接</button></div></div>
 <div class="prototype">
  <div class="app-top"><span class="app-name">N / 网络小说创作工作台</span><small>概念示意 · 所有内容与状态均为示例</small></div>
  <div role="tabpanel" id="panel-writing" aria-labelledby="tab-writing">
   <div class="app-tabs"><span>雾城账簿</span><span>荒原来信</span><span>本次目标：3 章</span></div>
   <div class="workspace"><aside class="book-nav"><span class="mini-label">本书目录</span><h4>第一卷 / 消失的账目</h4><div class="chapter-buttons"><button class="chapter-button" data-chapter="0" aria-pressed="false">01 雨夜来客<small>已保存 · 工作稿</small></button><button class="chapter-button" data-chapter="1" aria-pressed="false">02 缺失的名字<small>已保存 · 工作稿</small></button><button class="chapter-button" data-chapter="2" aria-pressed="true">03 钟楼的回声<small>已保存 · 待处理</small></button></div></aside>
    <div class="manuscript"><div class="chapter-meta"><span>正文</span><span>/</span><span class="mini-tag" id="chapter-state">候选稿 · 待处理</span></div><h4 id="chapter-title">第三章　钟楼的回声</h4><div class="sample-copy" id="chapter-copy"><p>钟声停了。林砚把账簿合上，潮湿的纸页黏住了他的指尖。钟楼下，巡夜人的灯正向北街移去。</p><p>他将手贴在铜门上，<mark>隔着门板，看见了里面的人影。</mark>那人怀里抱着的，正是失踪账房的印匣。</p><p>可他从未学过这样的术法。</p></div><p class="excerpt-note">示例片段 · 点击左侧章节查看不同内容状态。</p></div>
    <aside class="inspector"><div><h5>本书规则 / 应用记录</h5><div class="rule-row"><span>第三人称限知视角</span><span>已应用</span></div><div class="rule-row"><span>禁用表达清单</span><span>已检查</span></div><div class="rule-row"><span>低魔世界约束</span><span>已锁定</span></div></div><div class="review-issue" id="review-issue"><b>01 / 锁定设定冲突</b><span id="review-message">主角不具备感知类术法。候选新增能力与本书设定冲突，待作者处理。</span></div></aside>
   </div><div class="app-status"><span><i class="dot amber" id="state-dot"></i><span id="app-task-state">运行已暂停 · 第 3 章待处理</span></span><span>2 / 3 章工作稿已保存 · 尚未人工定稿</span></div>
  </div>
  <div role="tabpanel" id="panel-templates" aria-labelledby="tab-templates" hidden><div class="tool-content"><div class="tool-heading"><div><h4>创作模板库</h4><p>将世界观、文风和方法保存为可反复使用的方案。</p></div><span class="display-action">共享创作资产</span></div><div class="template-grid"><article class="template-card active"><div class="book-spine">雾</div><h5>低魔城邦 · 悬疑叙事</h5><p>稀缺术法、有限视角，围绕线索建立期待与信息差。</p><div class="tiny-tags"><span>世界观</span><span>文风</span><span>方法</span></div><footer>v1.0 · 已用于 2 本示例作品</footer></article><article class="template-card"><div class="book-spine gold">行</div><h5>冒险成长 · 阶段回报</h5><p>以目标、代价和阶段兑现组织人物的成长路径。</p><div class="tiny-tags"><span>剧情方法</span><span>规则</span></div><footer>v1.2 · 手工创建</footer></article><article class="template-card"><div class="book-spine gray">言</div><h5>克制叙事 · 文风规范</h5><p>让动作和对话承担情绪，用适量细节连接场景。</p><div class="tiny-tags"><span>文风</span><span>正反例</span></div><footer>v0.2 · 样文试写待校准</footer></article></div><p class="tool-bottom">采用时保存为本书副本。模板更新不会自动覆盖已有作品。</p></div></div>
  <div role="tabpanel" id="panel-models" aria-labelledby="tab-models" hidden><div class="tool-content"><div class="tool-heading"><div><h4>模型与连接</h4><p>配置一次，由每本作品选择自己的连接。</p></div><span class="display-action">连接配置示意</span></div><div class="connection-layout"><div><div class="connection-choice">DeepSeek 主连接<small>密钥已配置 · 示例状态</small></div></div><div><div class="connection-fields"><div class="field"><span class="field-label">连接名称</span><div class="field-value">DeepSeek 主连接</div></div><div class="field"><span class="field-label">服务商</span><div class="field-value">DeepSeek</div></div><div class="field full"><span class="field-label">模型与任务预设</span><div class="field-value">由用户配置型号 · 规划 / 正文 / 审校</div></div><div class="field"><span class="field-label">API Key</span><div class="field-value">已配置 · 不展示原密钥</div></div><div class="field"><span class="field-label">默认使用范围</span><div class="field-value">新书建议，已有书保留绑定</div></div></div><div class="connection-note">每次运行单独设置范围和预算。作品与模板不携带密钥，迁移后重新绑定连接。</div></div></div></div></div>
 </div><figcaption class="figure-note">示意图用于产品评审，未表示真实功能已经实现。界面切换仅改变本页展示，不发起模型请求。</figcaption>
</figure>
"""

FLOW = r"""
<figure class="figure" aria-labelledby="flow-heading"><div class="figure-head"><div><span class="figure-index">FIG. 02 / CREATION JOURNEY</span><h3 class="figure-title" id="flow-heading">从一次创作意图，到一批可审阅工作稿</h3><p class="figure-desc">作者明确边界，系统在边界内连续推进。</p></div></div><div class="flow-grid"><div class="flow-card"><span>01 / SETUP</span><h4>设定本次目标</h4><p>创意、模板与连接<br>章数、字数和预算</p></div><div class="flow-card"><span>02 / PLAN</span><h4>自动形成规划</h4><p>主线 → 分卷 → 章纲<br>带入规则与有效记忆</p></div><div class="flow-card"><span>03 / WRITE</span><h4>逐章循环推进</h4><p>生成 → 检查 → 有限修正<br>保存工作稿与运行记忆 ↺</p></div><div class="flow-card human"><span>04 / AUTHOR</span><h4>集中审阅定稿</h4><p>修改、返工或放弃<br>连续范围定稿与导出</p></div></div><div class="flow-sub"><div><b>通过检查 + 仍有章节 → 继续第 03 步</b>正常章节无需逐章点击接受。</div><div><b>冲突 / 预算不足 / 修正耗尽 → 暂停</b>保留已有成果，作者处理后再继续。</div></div><figcaption class="figure-note">生成完成只代表本次范围完成。工作稿纳入正式作品，需要作者明确选择。</figcaption></figure>
"""

ASSETS = r"""
<figure class="figure" aria-labelledby="asset-heading"><div class="figure-head"><div><span class="figure-index">FIG. 03 / REUSABLE ASSETS</span><h3 class="figure-title" id="asset-heading">复用创作方案，各本书独立生长</h3></div></div><div class="asset-diagram"><div class="asset-source"><span class="mini-label">共享模板库</span><h4>低魔城邦 · 悬疑叙事 v1</h4><p>世界观 + 文风 + 方法 + 规则<br>可命名、版本化、复制与归档</p></div><div class="asset-arrow">→<small>选取维度<br>复制为本书配置</small></div><div class="asset-books"><div class="asset-book"><b>作品 A / 雾城账簿</b><small>配置副本 A · 独立人物 · 正文与故事记忆 A</small></div><div class="asset-book"><b>作品 B / 荒原来信</b><small>配置副本 B · 独立人物 · 正文与故事记忆 B</small></div></div></div><figcaption class="figure-note">模板升级不会自动覆盖两本书；只采用文风时，不自动带入世界观。模型连接可复用，密钥与作品分开保存。</figcaption></figure>
"""

STATES = r"""
<figure class="figure" aria-labelledby="state-heading"><div class="figure-head"><div><span class="figure-index">FIG. 04 / CONTENT STATES</span><h3 class="figure-title" id="state-heading">保存了，不等于已经定稿</h3><p class="figure-desc">内容状态与保存状态、任务状态分别记录。</p></div></div><div class="version-flow"><div class="version-item"><small>生成或改写所得</small><h4>候选稿</h4><p>可能不完整或尚未检查<br>不作为已确认故事事实</p><span class="state-label">必要检查 + 策略采纳 →</span></div><div class="version-item"><small>系统在授权范围内采纳</small><h4>工作稿</h4><p>用于本次后续章节承接<br>与正式故事状态分开</p><span class="state-label">作者审阅 + 连续范围定稿 →</span></div><div class="version-item"><small>作者明确决定</small><h4>正式稿</h4><p>正式正文与故事状态同步<br>可以追溯、修订与回退</p><span class="state-label">作者保留最终决定权</span></div></div><figcaption class="figure-note">放弃工作稿不改变正式稿。前文或规范变化时，旧候选需复核后才能继续使用。</figcaption></figure>
"""

ROADMAP = r"""
<figure class="figure" aria-labelledby="roadmap-heading"><span class="figure-index">FIG. 05 / PRODUCT ROADMAP</span><h3 class="figure-title" id="roadmap-heading">先跑通创作，再验证长篇质量</h3><div class="roadmap"><div class="roadmap-item"><span>P0</span><h4>本地基础</h4><p>项目与保存<br>模板、连接、规则、修订</p><small>验证：离线可用与资产隔离</small></div><div class="roadmap-item focus"><span>P1</span><h4>首个可用版本</h4><p>3–5 章连续创作<br>材料、文风、预算与定稿</p><small>验证：完整使用流程与样稿</small></div><div class="roadmap-item"><span>P2</span><h4>长篇质量</h4><p>30–50 章验证<br>历史状态与反馈改进</p><small>验证：长期执行与修改成本</small></div><div class="roadmap-item"><span>P3</span><h4>按需求扩展</h4><p>参考小说提炼模板<br>更多格式与跨插件工作流</p><small>验证：新增能力的实际收益</small></div></div><figcaption class="figure-note">路线为规划阶段，不表示当前进度或已承诺的交付日期。</figcaption></figure>
"""

JS = r"""
(() => {
 const tabs = [...document.querySelectorAll('[role="tab"]')];
 function selectTab(tab, focus=false) {
  tabs.forEach(t => { const on=t===tab; t.setAttribute('aria-selected',String(on)); t.tabIndex=on?0:-1; document.getElementById(t.getAttribute('aria-controls')).hidden=!on; });
  if(focus) tab.focus();
 }
 tabs.forEach((tab,i) => {
  tab.addEventListener('click',()=>selectTab(tab));
  tab.addEventListener('keydown',e=>{let n=null; if(e.key==='ArrowRight')n=(i+1)%tabs.length; if(e.key==='ArrowLeft')n=(i+tabs.length-1)%tabs.length; if(e.key==='Home')n=0; if(e.key==='End')n=tabs.length-1; if(n!==null){e.preventDefault();selectTab(tabs[n],true);}});
 });
 const chapters=[
  {title:'第一章　雨夜来客',state:'工作稿 · 已通过检查',copy:['最后一盏街灯熄灭时，有人敲响了账房的门。','林砚放下笔。门外站着一名披灰斗篷的女人，雨水顺着她的袖口落在门槛上。她递来一张收据，签名处只有半枚印章。','那枚印章，他下午才在失踪师傅的抽屉里见过。'],issue:'本章必要检查通过，已保存为工作稿。后续章节可承接该章事件；正式稿尚未改变。'},
  {title:'第二章　缺失的名字',state:'工作稿 · 已通过检查',copy:['收据编号连着北街的仓库。林砚翻开旧账，在同一页上找到三笔相同的款项。','每笔款项后面，都少了一个名字。纸上没有刮擦，像是落笔的人一开始就留下了空位。','他将空位逐一圈起。第三个圆旁，师傅留下了一枚极小的钟形记号。'],issue:'本章线索承接第 1 章，新的钟楼线索已进入工作稿记忆。尚未人工定稿。'},
  {title:'第三章　钟楼的回声',state:'候选稿 · 待处理',copy:['钟声停了。林砚把账簿合上，潮湿的纸页黏住了他的指尖。钟楼下，巡夜人的灯正向北街移去。','他将手贴在铜门上，隔着门板，看见了里面的人影。那人怀里抱着的，正是失踪账房的印匣。','可他从未学过这样的术法。'],issue:'主角不具备感知类术法。候选新增能力与本书设定冲突，待作者处理。'}
 ];
 const buttons=[...document.querySelectorAll('[data-chapter]')];
 buttons.forEach(button=>button.addEventListener('click',()=>{
  const index=Number(button.dataset.chapter),c=chapters[index];
  buttons.forEach(b=>b.setAttribute('aria-pressed',String(b===button)));
  document.getElementById('chapter-title').textContent=c.title;
  document.getElementById('chapter-state').textContent=c.state;
  const copy=document.getElementById('chapter-copy');copy.replaceChildren();
  c.copy.forEach((text,i)=>{const p=document.createElement('p');if(index===2&&i===1){const phrase='隔着门板，看见了里面的人影。';const [a,b]=text.split(phrase);p.append(a);const mark=document.createElement('mark');mark.textContent=phrase;p.append(mark,b);}else p.textContent=text;copy.append(p);});
  const issue=document.getElementById('review-issue');issue.classList.toggle('clear',index<2);issue.querySelector('b').textContent=index<2?'本章 / 检查通过':'01 / 锁定设定冲突';document.getElementById('review-message').textContent=c.issue;
  document.getElementById('preview-announcement').textContent=c.title+'，'+c.state;
 }));
 document.getElementById('print-button').addEventListener('click',()=>window.print());
 let previousTab;
 window.addEventListener('beforeprint',()=>{previousTab=tabs.find(t=>t.getAttribute('aria-selected')==='true');document.querySelectorAll('[role="tabpanel"]').forEach(p=>p.hidden=false);});
 window.addEventListener('afterprint',()=>{if(previousTab)selectTab(previousTab);});
 const sections=[...document.querySelectorAll('main>section[id]')];
 const links=[...document.querySelectorAll('.toc a')];let scheduled=false;
 function updateReading(){scheduled=false;let current='overview';for(const s of sections){if(s.getBoundingClientRect().top<180)current=s.id;}links.forEach(a=>{const on=a.hash==='#'+current;a.classList.toggle('active',on);if(on)a.setAttribute('aria-current','location');else a.removeAttribute('aria-current');});const available=document.documentElement.scrollHeight-innerHeight;const progress=available>0?Math.min(100,Math.max(0,100*scrollY/available)):0;document.getElementById('reading-progress').style.width=progress+'%';document.getElementById('reading-percent').textContent=Math.round(progress)+'%';}
 addEventListener('scroll',()=>{if(!scheduled){scheduled=true;requestAnimationFrame(updateReading);}}, {passive:true});addEventListener('resize',updateReading);updateReading();
 document.querySelectorAll('.mobile-nav a').forEach(a=>a.addEventListener('click',()=>document.querySelector('.mobile-nav').open=false));
})();
"""


def inline(text):
    """Escape source text, then render inline markup used in this document."""
    tokens = []

    def save(markup):
        tokens.append(markup)
        return f"\x00{len(tokens)-1}\x00"

    text = re.sub(r"`([^`]+)`", lambda m: save("<code>" + html.escape(m[1]) + "</code>"), text)
    # 原生截图嵌入 HTML，复制单个 HTML 文件仍可离线阅读；Markdown 同时保留独立图片。
    def capture(match):
        path = (ROOT / match[2]).resolve()
        if not path.is_relative_to((ROOT / "assets").resolve()) or path.suffix.lower() != ".png":
            raise ValueError("产品截图必须位于 assets 下并使用 PNG")
        encoded = base64.b64encode(path.read_bytes()).decode("ascii")
        return save('<img class="native-capture" src="data:image/png;base64,' + encoded + '" alt="' + html.escape(match[1], quote=True) + '">')
    text = re.sub(r"!\[([^\]]+)\]\(([^)]+)\)", capture, text)
    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", lambda m: save('<a href="' + html.escape(m[2], quote=True) + '">' + html.escape(m[1]) + '</a>'), text)
    text = html.escape(text)
    text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", text)
    return re.sub(r"\x00(\d+)\x00", lambda m: tokens[int(m[1])], text)


def render_blocks(lines):
    output, i = [], 0
    while i < len(lines):
        line = lines[i]
        if not line.strip():
            i += 1
            continue
        if line.startswith("|"):
            rows = []
            while i < len(lines) and lines[i].startswith("|"):
                row = [s.strip() for s in lines[i].strip().strip("|").split("|")]
                if not all(re.fullmatch(r":?-+:?", cell) for cell in row):
                    rows.append(row)
                i += 1
            output.append('<div class="table-wrap" role="region" aria-label="文档表格，可横向滚动" tabindex="0"><table><thead><tr>' + ''.join('<th scope="col">' + inline(c) + '</th>' for c in rows[0]) + '</tr></thead><tbody>')
            for row in rows[1:]:
                output.append('<tr>' + ''.join('<td>' + inline(c) + '</td>' for c in row) + '</tr>')
            output.append('</tbody></table></div>')
            continue
        heading = re.match(r"^(#{3,6}) (.*)$", line)
        if heading:
            level = len(heading[1])
            title = inline(heading[2])
            title = re.sub(r"〔(.*?)〕", r'<span class="req-phase">\1</span>', title)
            output.append(f'<h{level}>{title}</h{level}>')
            i += 1
            continue
        list_match = re.match(r"^(?:([-]) |(\d+)\. )(.*)$", line)
        if list_match:
            ordered = list_match[2] is not None
            tag = 'ol' if ordered else 'ul'
            output.append(f'<{tag}>')
            while i < len(lines):
                match = re.match(r"^(?:([-]) |(\d+)\. )(.*)$", lines[i])
                if not match or (match[2] is not None) != ordered:
                    break
                output.append('<li>' + inline(match[3]) + '</li>')
                i += 1
            output.append(f'</{tag}>')
            continue
        paragraph = []
        while i < len(lines) and lines[i].strip() and not re.match(r"^(#{2,6} |\||- |\d+\. )", lines[i]):
            raw = lines[i]
            paragraph.append(inline(raw.rstrip()) + ('<br>' if raw.endswith('  ') else ''))
            i += 1
        if not paragraph:
            raise ValueError(f"Unsupported Markdown line: {lines[i]}")
        output.append('<p>' + '\n'.join(paragraph) + '</p>')
    return '\n'.join(output)


def build():
    source = SOURCE.read_text(encoding='utf-8-sig')
    chunks = re.split(r'(?m)^## (.+)\n', source)
    intro_lines = chunks[0].splitlines()[1:]
    nav = ['<a href="#overview" class="active"><span>00</span>产品概览</a>']
    sections = []
    diagrams = {3: WORKSPACE, 4: FLOW, 6: STATES, 10: ROADMAP}
    for index in range(1, len(chunks), 2):
        title, content = chunks[index].strip(), chunks[index+1]
        number = int(re.match(r'(\d+)\.', title)[1])
        label = re.sub(r'^\d+\.\s*', '', title)
        nav.append(f'<a href="#section-{number}"><span>{number:02d}</span>{html.escape(label)}</a>')
        section_body = render_blocks(content.splitlines())
        if number == 5:
            parts = re.split(r'(?=<h3>FR-)', section_body)
            section_body = parts[0] + '\n' + '\n'.join('<article class="requirement" id="fr-' + re.search(r'FR-(\d+)', part)[1] + '">' + part + '</article>' for part in parts[1:])
            section_body = ASSETS + section_body
        else:
            section_body = diagrams.get(number, '') + section_body
        sections.append(f'<section class="doc-section" id="section-{number}" aria-labelledby="heading-{number}"><span class="section-count">PRODUCT REQUIREMENTS / {number:02d}</span><h2 id="heading-{number}">{html.escape(title)}</h2>{section_body}</section>')
    toc = '\n'.join(nav)
    mobile_toc = toc.replace(' class="active"', '')
    digest = hashlib.sha256(source.encode('utf-8')).hexdigest()
    result = '''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><meta name="description" content="网络小说创作工作台产品需求文档：用户场景、交互示意、自动创作流程、11 组功能需求与产品验收。"><meta name="source-sha256" content="''' + digest + '''"><title>网络小说创作工作台 · 产品需求文档</title><style>''' + CSS + '''</style></head>
<body><a class="skip" href="#main-content">跳转到产品文档</a>
<header class="topbar"><a class="brand" href="#overview"><span class="brand-mark" aria-hidden="true">N</span><span><strong>网络小说创作工作台</strong><small>NOVEL WORKBENCH / PRODUCT</small></span></a><div class="top-actions"><span class="status-chip">工程候选 · 待产品签收</span><a href="novel-generation-product-requirements.md">Markdown 原文 ↗</a><button class="plain-button" id="print-button" type="button">打印 / 存为 PDF</button></div></header>
<details class="mobile-nav"><summary>阅读目录</summary><nav aria-label="移动端目录">''' + mobile_toc + '''</nav></details>
<div class="layout"><aside class="sidebar"><div><p class="nav-kicker">DOCUMENT / V0.2</p><nav class="toc" aria-label="文档目录">''' + toc + '''</nav></div><div class="side-note">MYAVALONIA NOVEL GENERATE<br>2026.09.12 · 工程验收记录<div class="reading-line"><span id="reading-progress"></span></div><span>阅读进度 <span id="reading-percent">0%</span></span></div></aside>
<main class="content" id="main-content"><section class="hero" id="overview" aria-labelledby="product-title"><div class="hero-grid"><div><div class="eyebrow">PRODUCT BRIEF / 产品需求文档</div><h1 id="product-title">让创意成为<br>可持续创作的长篇。</h1><p class="hero-lead">面向个人中文网文创作者，把创意、规范和故事记忆放进同一个工作台。在明确范围与预算内连续起草，由作者掌握方向和最终定稿。</p></div><div class="hero-flow" aria-label="创意经过受限自动创作成为工作稿，作者审阅后形成正式稿"><div class="hero-flow-item"><span>01</span><div><b>作者给出创意与边界</b><small>方向 / 规则 / 范围 / 预算</small></div></div><div class="down" aria-hidden="true">↓</div><div class="hero-flow-item"><span>02</span><div><b>系统连续生成工作稿</b><small>规划 / 起草 / 检查 / 修正</small></div></div><div class="down" aria-hidden="true">↓</div><div class="hero-flow-item"><span>03</span><div><b>作者审阅与正式定稿</b><small>修改 / 选择 / 回退 / 导出</small></div></div></div></div><div class="meta-strip"><span><b>11</b>组功能需求</span><span><b>16</b>个首版验收场景</span><span><b>05</b>组产品示意</span><span><b>P0–P3</b>版本路线</span></div><p class="hero-caption"><span class="status-chip">工程候选</span><span>首版工程功能及真实三章生成已验证；作者签收和完整桌面验收待补。概念示意与真实证据分别标注。</span></p></section>
<div class="intro" aria-label="原文说明">''' + render_blocks(intro_lines) + '''</div>
''' + '\n'.join(sections) + '''
<footer class="page-footer"><span>网络小说创作工作台 · 产品需求文档 v0.2</span><span>正文依据 Markdown 原文 · 示意图用于产品评审</span><a href="#overview">返回顶部 ↑</a></footer></main></div><div class="screen-reader" id="preview-announcement" role="status" aria-live="polite"></div>
<script>''' + JS + '''</script></body></html>
'''
    OUTPUT.write_text(result, encoding='utf-8')
    print(f'Generated: {OUTPUT}\nSource sections: {(len(chunks)-1)//2}; figures: 5; bytes: {OUTPUT.stat().st_size}')


if __name__ == '__main__':
    build()
