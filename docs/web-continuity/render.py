from pathlib import Path
import base64,subprocess,json,hashlib,shutil,argparse,csv,tempfile
parser=argparse.ArgumentParser(description="Render the local Zibashe handbook; no ERP network access")
parser.add_argument('--output',required=True);args=parser.parse_args()
docs=Path(__file__).resolve().parent;root=docs.parents[1];art=Path(args.output).resolve();art.mkdir(parents=True,exist_ok=True)
with (docs/'capability-register.csv').open(encoding='utf-8-sig',newline='') as f:rows=list(csv.DictReader(f))
assert len({r['شناسه'] for r in rows})==len(rows)
main=(docs/'handbook.fa.md').read_text().split('# پیوست الف — رجیستر تک‌تک قابلیت‌ها')[0]
appendix=['# پیوست الف — رجیستر تک‌تک قابلیت‌ها\n'];last=None
for r in rows:
 if last!=r['حوزه']:last=r['حوزه'];appendix.append('\n## '+last+'\n')
 appendix.append('\n### '+r['شناسه']+' — '+r['قابلیت']+'\n\n**نقش:** '+r['نقش']+' · **مرحله:** '+r['مرحله']+' · **وضعیت:** '+r['وضعیت وب']+'\n\n**پذیرش:** '+r['معیار پذیرش']+'\n\nمبنای تطبیق: '+r['مبنای فعلی / محل بررسی']+'\n\n')
(docs/'handbook.fa.md').write_text((main+'\n'.join(appendix)).rstrip()+'\n')
(docs/'capability-register.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2)+'\n')
font=base64.b64encode(Path('/usr/share/fonts/truetype/noto/NotoSansArabic-Regular.ttf').read_bytes()).decode();bold=base64.b64encode(Path('/usr/share/fonts/truetype/noto/NotoSansArabic-Bold.ttf').read_bytes()).decode()
css='''@font-face{font-family:BookArabic;src:url(data:font/ttf;base64,FONT)}@font-face{font-family:BookArabic;src:url(data:font/ttf;base64,BOLD);font-weight:700}
@page{size:A4;margin:17mm 14mm 18mm;@bottom-center{content:counter(page);font-family:BookArabic;font-size:9pt;color:#64748b}}
*{box-sizing:border-box}html{direction:rtl}body{font-family:BookArabic,sans-serif;color:#16283c;line-height:1.85;font-size:10.5pt;margin:0;background:#fff}h1,h2,h3{color:#123c54;line-height:1.6;break-after:avoid-page;page-break-after:avoid}h1{font-size:22pt;border-bottom:3px solid #24a699;padding-bottom:12px;margin:24px 0}h2{font-size:15pt;margin-top:25px;border-bottom:1px solid #cad8e2;padding-bottom:7px}h3{font-size:12pt;margin-top:20px}p{margin:8px 0;orphans:3;widows:3}strong{font-weight:700}a{color:#126e8d;text-decoration:none}table{width:100%;border-collapse:collapse;margin:15px 0;font-size:8.8pt;table-layout:fixed}th,td{border:1px solid #cbd5e1;padding:7px;vertical-align:top;overflow-wrap:anywhere}th{background:#e9f3f5;color:#123c54}tbody tr:nth-child(even){background:#f6f9fb}tr{break-inside:avoid}thead{display:table-header-group}pre{direction:ltr;text-align:left;white-space:pre-wrap;overflow-wrap:anywhere;background:#f0f5f9;border:1px solid #cbd5e1;padding:12px;font-size:9pt;line-height:1.6;unicode-bidi:embed}code{font-family:monospace;overflow-wrap:anywhere}p code,li code{direction:ltr;unicode-bidi:embed;font-size:9pt}li{margin-bottom:5px}#title-block-header{min-height:245mm;padding:65mm 8mm 20mm;page-break-after:always;background:linear-gradient(160deg,#edf8f7,#fff)}#title-block-header::before{content:"ZIBASHE · ENGINEERING HANDBOOK";display:block;direction:ltr;text-align:left;color:#087f80;font-family:sans-serif;font-size:11pt;letter-spacing:2px}#title-block-header h1{font-size:29pt;border:0;margin-top:20mm}#title-block-header::after{content:"نسخهٔ ۱٫۰ | ۱۹۰ قابلیت | ۱۰ مرحلهٔ اجرا\\A استمرار خدمت با اینترنت داخلی\\A انتقال کنترل‌شده برای حدود ۱۰۰۰ مشتری فعال\\A ۱۸ نوامبر ۲۰۲۶";white-space:pre-line;display:block;line-height:2.4;color:#486477;margin-top:18mm}nav#TOC{page-break-after:always}nav#TOC ul{padding-right:20px}nav#TOC a{color:#24586c;font-size:10pt}body>h1:not(.title){break-before:page}h3+ p{break-after:avoid-page} @media screen{body{max-width:1080px;margin:25px auto;padding:20px;box-shadow:0 0 20px #dbe4eb}#title-block-header{min-height:600px;padding:90px 35px}table{font-size:12px}}'''.replace('FONT',font).replace('BOLD',bold)
(art/'print.css').write_text(css)
subprocess.run(['pandoc',str(docs/'handbook.fa.md'),'--standalone','--toc','--toc-depth=2','--metadata','pagetitle=دفترچه فنی وب مستقل زیباشی','--css','print.css','--output',str(art/'zibashe-web-handbook.html')],check=True)
p=art/'zibashe-web-handbook.html';s=p.read_text();s=s.replace('<link rel="stylesheet" href="print.css" />','<style>'+css+'</style>');assert '<link rel="stylesheet"' not in s;p.write_text(s)
for a,b in [('handbook.fa.md','zibashe-web-handbook.fa.md'),('capability-register.csv','zibashe-capabilities-190.csv'),('execution-gates.csv','zibashe-execution-gates.csv')]:shutil.copyfile(docs/a,art/b)
print(json.dumps({'html_bytes':p.stat().st_size,'self_contained':True}))

from weasyprint import HTML
HTML(string=(art/'zibashe-web-handbook.html').read_text(),base_url=str(art)).write_pdf(art/'zibashe-web-handbook.pdf')
assert (art/'zibashe-web-handbook.pdf').stat().st_size>10000
manifest={'version':'1.0','features':len(rows),'files':{f.name:hashlib.sha256(f.read_bytes()).hexdigest() for f in art.iterdir() if f.is_file() and f.suffix in ['.pdf','.html','.md','.csv']}}
(art/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps({'pdf':str(art/'zibashe-web-handbook.pdf'),'features':len(rows)}))
