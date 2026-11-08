#!/usr/bin/env python3
"""One illustrated fragrance guide every two hours, offset one hour from direct ads."""
import argparse
import fcntl
import html
import importlib.util
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import sqlite3
import time

STATE = Path.home() / '.local/state/zibashe-education'
DEPLOY = Path('/opt/zibashe/app/ZibasheERP/deploy')
DIGITS = str.maketrans('0123456789', '۰۱۲۳۴۵۶۷۸۹')
# Tags select examples from their actual catalogue notes; empty tags allow a comparison across styles.
TOPICS = [
 ('cool', 'برای هوای خنک چه عطری انتخاب کنم؟', 'هوای خنک فرصت خوبیه برای امتحان وانیل، چوب و ادویه؛ ولی انتخابت رو از سلیقهٔ خودت شروع کن. برای فضای بسته از مقدار کم شروع کن و قبل از خرید روی پوست تست کن.', ['وانیل','چوب','چرم','کهربا']),
 ('clean', 'رایحهٔ تمیز یعنی چی؟', 'وقتی از بوی تمیز حرف می‌زنیم، ممکنه منظورمون حس صابون، پارچهٔ تازه یا مشک نرم باشه؛ این‌ها یک بو نیستن! موقع تست ببین کدوم حس رو دوست داری. «تمیز» بودن هم به معنی ضعیف بودن عطر نیست.', ['مشک','زنبق','چای','آلدهید']),
 ('office', 'برای محل کار چطور عطر بزنیم؟', 'در فضای مشترک، مقدار کم معمولاً شروع بهتریه. اول کم بزن و فرصت بده رایحه جا بیفته؛ اگر لازم بود بعداً اضافه کن. انتخاب عطر و مقدار مصرف هر دو مهم‌اند، حتی رایحهٔ موردعلاقه‌مون هم نباید همکارها رو کلافه کنه.', []),
 ('vanilla', 'همهٔ عطرهای وانیلی شبیه هم‌اند؟', 'نه لزوماً! وانیل کنار کارامل می‌تونه حس خوراکی بده و کنار چوب یا تنباکو حال‌وهوای متفاوتی پیدا کنه. موقع انتخاب، فقط کلمهٔ وانیل رو نبین؛ به همراه‌های اون هم دقت کن.', ['وانیل']),
 ('wood', 'عطر چوبی یعنی بوی چوب خالص؟', 'رایحهٔ چوبی می‌تونه نرم، خشک، روشن یا دودی حس بشه. سدر و چوب صندل هم الزاماً یک حس ندارن. برای شناخت سلیقه‌ات، دو عطر چوبی متفاوت رو جدا تست کن و نتیجه رو با حس خودت بسنج.', ['چوب','سدر','وتیور','صندل']),
 ('sweet', 'عطر شیرین دوست دارم؛ از کجا شروع کنم؟', 'شیرینی عطر فقط وانیل نیست؛ میوه، کارامل، تونکا و حس‌های خوراکی هم نقش دارن. اول ببین شیرینی میوه‌ای رو ترجیح می‌دی یا حس دسر و خامه. تست کم‌حجم کمک می‌کنه انتخاب دقیق‌تری داشته باشی.', ['وانیل','کارامل','تونکا','میوه','پسته']),
 ('leather', 'رایحهٔ چرمی به سلیقهٔ من می‌خوره؟', 'چرم در عطر ممکنه نرم و جیرمانند باشه یا حس تیره‌تر و خشک‌تری بده. اگر هنوز با این خانواده آشنا نیستی، از تست کم‌حجم شروع کن و به رایحه فرصت بده روی پوستت تغییر کنه.', ['چرم','جیر']),
 ('floral', 'عطر گلی همیشه ملایمه؟', 'گل‌ها دنیای بزرگی دارن؛ رز، یاس و مریم لزوماً یک حس ایجاد نمی‌کنن. برای انتخاب، بپرس گلیِ شفاف و تازه می‌خوای یا گلیِ شیرین و پررنگ؟ اسم خانواده به‌تنهایی شدت و ماندگاری رو تعیین نمی‌کنه.', ['گلی','گل','رز','یاس','مریم']),
 ('fruity', 'میوه‌ای دوست دارم، اما خیلی شیرین نه!', 'رایحهٔ میوه‌ای می‌تونه ترش، آبدار یا شیرین حس بشه. به میوه‌های کنار هم و پایهٔ عطر دقت کن؛ چوب و مشک می‌تونن تجربهٔ متفاوتی بسازن. قبل از تصمیم، هم شروع و هم ادامهٔ رایحه رو امتحان کن.', ['میوه','تمشک','سیب','هلو','انگور','ترنج']),
 ('powder', 'بوی پودری یعنی چی؟', 'بعضی رایحه‌ها یادآور پودر، لوازم آرایش یا لطافت پارچه‌ان. این حس ممکنه در کنار زنبق، بنفشه یا مشک دیده بشه، اما هر عطر پودری یک شخصیت داره. اگر این حس رو دوست داری، چند نمونهٔ متفاوت رو مقایسه کن.', ['پودری','زنبق','بنفشه','ایریس']),
 ('skin', 'چرا عطر روی پوست من فرق می‌کنه؟', 'مقدار مصرف، شرایط پوست و محیط می‌تونن روی تجربهٔ بو اثر بذارن. برای انتخاب شخصی، فقط به بوی کاغذ یا نظر دیگران اکتفا نکن؛ روی پوست خودت تست کن و چند ساعت بهش فرصت بده.', []),
 ('nose', 'چرا بعد از مدتی عطر خودم رو حس نمی‌کنم؟', 'بینی ممکنه به بوی تکراری عادت کنه؛ حس‌نکردن عطر همیشه به معنی تمام‌شدنش نیست. قبل از اضافه‌کردن مقدار زیاد، کمی فاصله بگیر یا از یک نفر نظر بخواه. مخصوصاً در فضای بسته با مقدار کم شروع کن.', []),
 ('testing', 'چطور دو عطر رو درست مقایسه کنیم؟', 'هر بار تعداد کمی عطر تست کن و جای هر کدوم رو یادت نگه دار. فقط اولین لحظه رو قضاوت نکن؛ شروع، ادامه و حس باقی‌مانده رو جدا بسنج. برای تصمیم مهم، تست در روزهای جدا هم کمک می‌کنه.', []),
 ('gift', 'برای هدیه‌دادن عطر از چی شروع کنم؟', 'به بوهایی که طرف مقابل قبلاً دوست داشته توجه کن، نه فقط اسم برند و ظاهر شیشه. اگر سلیقه‌اش رو خوب نمی‌شناسی، چند تست کوچک برای انتخاب خودش می‌تونه تجربهٔ بهتری بسازه.', []),
 ('labels', 'ادوتویلت یا ادوپارفام؛ کدوم بهتره؟', 'این عنوان‌ها به‌تنهایی برنده رو تعیین نمی‌کنن. دو نسخه از یک عطر ممکنه حتی ترکیب و حس متفاوتی داشته باشن. نسخهٔ دقیق رو تست کن و انتخابت رو با رایحه و کاربردت بسنج، نه فقط نوشتهٔ روی شیشه.', []),
 ('storage', 'عطر رو کجا نگه داریم؟', 'جای خنک، دور از نور مستقیم و نوسان زیاد دما انتخاب بهتریه. داخل ماشین و کنار پنجرهٔ آفتاب‌گیر معمولاً جای مناسبی نیست. در شیشه رو هم بعد از مصرف ببند تا از رایحهٔ موردعلاقه‌ات بهتر مراقبت کنی.', []),
 ('amber', 'وقتی می‌گیم عطر کهربایی، منظور چیه؟', 'کهربایی معمولاً توصیف یک حال‌وهوای گرم، رزینی و گاهی شیرینه؛ الزاماً بوی یک مادهٔ واحد نیست. به نت‌های همراه نگاه کن تا بفهمی این گرما کنار گل، چوب یا وانیل چه حسی می‌سازه.', ['کهربا','عنبر','بنزوئین','لابدانوم','صمغ']),
 ('wardrobe', 'یک عطر برای همهٔ موقعیت‌ها لازمه؟', 'می‌تونی یک رایحهٔ محبوب داشته باشی یا برای موقعیت‌های مختلف انتخاب‌های متفاوت بچینی. اول کاربردهای واقعی خودت رو بشناس: محل کار، بیرون و مهمانی. تعداد کمتر با انتخاب آگاهانه هم می‌تونه کاملاً کافی باشه.', []),
]


def publisher():
    path = Path(__file__).with_name('marketing-publisher.py')
    if not path.exists(): path = DEPLOY / 'marketing-publisher.py'
    spec = importlib.util.spec_from_file_location('education_marketing', path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


def slot_for(now): return int((now - 3600) // 7200)


def select_rows(rows, topic, counts, last_seen, adapter):
    unique = {}
    for row in rows:
        if not adapter.eligible(row, 2**31): continue
        key = adapter.perfume_frequency_key(row)
        if counts.get(key, 0) >= 2: continue
        notes = ' '.join(str(row.get(field) or '') for field in ['Accords','TopNotes','MiddleNotes','BaseNotes'])
        if topic[3] and not any(tag in notes for tag in topic[3]): continue
        # Avoid showing several cycles of the same fragrance in one guide.
        rank = (row['TotalVolume']-row['ReservedVolume'], -int(row.get('TelegramMessageId') or 0))
        if key not in unique or rank < unique[key][0]: unique[key] = (rank, row)
    values = [pair[1] for pair in unique.values()]
    values.sort(key=lambda row: (row['TotalVolume']-row['ReservedVolume'] >= 20,
                                last_seen.get(adapter.perfume_frequency_key(row), 0),
                                row['TotalVolume']-row['ReservedVolume'], row['Id']))
    return values[:3]


def title(row): return row.get('PersianName') or row['EnglishName']
def code(row): return str(row.get('StablePublicCode') or row['PublicCode']).translate(DIGITS)
def volume(row): return str(row['TotalVolume']-row['ReservedVolume']).translate(DIGITS)
def link(row): return 'https://t.me/c/'+str(row['TelegramChannelId'])[4:]+'/'+str(int(row['TelegramMessageId']))


def guide(topic, rows):
    text = f'🌿 <b>{html.escape(topic[1])}</b>\n\n{html.escape(topic[2])}\n\n<b>سه گزینهٔ موجود برای امتحان و مقایسه:</b>\n'
    for index,row in enumerate(rows,1):
        notes = str(row.get('Accords') or row.get('TopNotes') or '').strip()[:110]
        text += f'\n{str(index).translate(DIGITS)}. <b>{html.escape(title(row))}</b>\n'
        if notes: text += html.escape(notes)+'\n'
        text += f'کد {code(row)} · باقی‌ماندهٔ زمان انتشار: {volume(row)} میل\n'
    text += '\nعکس هر سه عطر رو زیر همین پیام ببین؛ برای دیدن لیست و ثبت درخواست، گزینهٔ دلخواهت رو انتخاب کن 👇\n#راهنمای_عطر_زیباشی'
    if len(text.encode('utf-16-le'))//2 > 4096: raise ValueError('Guide exceeds Telegram limit')
    return text


def media(rows):
    return [{'type':'photo','media':row['TelegramPhotoFileId'], 'parse_mode':'HTML',
             'caption':f'<b>{html.escape(title(row))}</b>\nکد {code(row)} · {volume(row)} میل باقی‌مانده\n<a href="{link(row)}">دیدن لیست و ثبت درخواست</a>'} for row in rows]


def send_album(adapter, cfg, rows, parent):
    result=adapter.post('https://api.telegram.org/bot'+cfg['Telegram__BotToken']+'/sendMediaGroup',
                        {'chat_id':adapter.CHAT,'media':media(rows),
                         'reply_parameters':{'message_id':parent,'allow_sending_without_reply':False}}, timeout=45)
    if not result.get('ok') or len(result.get('result',[])) != 3: raise RuntimeError('Album receipt invalid')
    if any(str(item['chat']['id']) != adapter.CHAT for item in result['result']): raise RuntimeError('Album destination mismatch')
    return [item['message_id'] for item in result['result']]


def schema(db):
    db.execute('CREATE TABLE IF NOT EXISTS education_campaigns(slot INTEGER PRIMARY KEY,status TEXT,topic TEXT,rows_json TEXT,text_id INTEGER,album_ids TEXT,at REAL,error TEXT)')


def run(adapter, db, cfg, now, preview=False):
    schema(db); slot=slot_for(now)
    existing=db.execute('SELECT status FROM education_campaigns WHERE slot=?',(slot,)).fetchone()
    if existing and existing[0] != 'text_sent': return {'status':'slot_already_handled','slot':slot}
    if existing:
        saved=db.execute('SELECT topic,rows_json,text_id FROM education_campaigns WHERE slot=?',(slot,)).fetchone()
        rows=json.loads(saved[1]); topic=next(value for value in TOPICS if value[0]==saved[0]); text_id=saved[2]
    else:
        counts=dict(db.execute("SELECT perfume_key,COUNT(*) FROM slots WHERE at>? AND status IN ('sent','sending','unknown') AND perfume_key IS NOT NULL GROUP BY perfume_key",(now-86400,)))
        last_seen=dict(db.execute("SELECT perfume_key,MAX(at) FROM slots WHERE at>? AND status IN ('sent','sending','unknown') AND perfume_key IS NOT NULL GROUP BY perfume_key",(now-8*3600,)))
        used=dict(db.execute("SELECT topic,MAX(at) FROM education_campaigns WHERE status IN ('sent','sending_text','text_sent','sending_album','unknown') GROUP BY topic"))
        candidates=adapter.query(adapter.config('deploy-sqlserver-1'),maximum_remaining=2**31)
        topic=None; rows=[]
        for option in sorted(TOPICS,key=lambda value: used.get(value[0],0)):
            found=select_rows(candidates,option,counts,last_seen,adapter)
            if len(found)==3:topic,rows=option,found;break
        if not topic: return {'status':'waiting_for_three_available_fragrances','slot':slot}
        text_id=None
    # Refresh all three products immediately before publishing; exclusions apply to both phases.
    fresh=[]
    sqlenv=adapter.config('deploy-sqlserver-1')
    for row in rows:
        values=adapter.query(sqlenv,list_id=row['Id'],maximum_remaining=2**31)
        if len(values)!=1 or not adapter.eligible(values[0],2**31):return {'status':'selection_changed_retry_later','slot':slot}
        fresh.append(values[0])
    rows=fresh; text=guide(topic,rows)
    if preview:return {'status':'preview_no_messages_sent','topic':topic[0],'text':text,'codes':[code(row) for row in rows]}
    if not text_id:
        exposure=[]
        for index,row in enumerate(rows):
            key=-int(now*1000)-index
            while db.execute('SELECT 1 FROM slots WHERE slot=?',(key,)).fetchone():key-=3
            db.execute('INSERT INTO slots(slot,status,list_id,at,message_id,error,perfume_key) VALUES(?,?,?,?,?,?,?)',
                       (key,'sending',row['Id'],now,None,'education:'+str(slot),adapter.perfume_frequency_key(row)))
            exposure.append(key)
        db.execute('INSERT INTO education_campaigns(slot,status,topic,rows_json,at) VALUES(?,?,?,?,?)',
                   (slot,'sending_text',topic[0],json.dumps(rows,ensure_ascii=False),now));db.commit()
        try:
            reply=adapter.post('https://api.telegram.org/bot'+cfg['Telegram__BotToken']+'/sendMessage',
                {'chat_id':adapter.CHAT,'text':text,'parse_mode':'HTML','link_preview_options':{'is_disabled':True},
                 'reply_markup':{'inline_keyboard':[[{'text':title(row)[:40]+' | '+code(row),'url':link(row)}] for row in rows]}},timeout=45)
            if not reply.get('ok') or str(reply['result']['chat']['id'])!=adapter.CHAT:raise RuntimeError('Guide receipt invalid')
            text_id=reply['result']['message_id']
        except Exception:
            db.execute("UPDATE education_campaigns SET status='unknown',error='Text delivery uncertain; no blind retry' WHERE slot=?",(slot,))
            for key in exposure:db.execute("UPDATE slots SET status='unknown' WHERE slot=?",(key,))
            db.commit();raise RuntimeError('Guide delivery uncertain; not automatically repeated') from None
        for key in exposure:db.execute("UPDATE slots SET status='sent',message_id=? WHERE slot=?",(text_id,key))
        db.execute("UPDATE education_campaigns SET status='text_sent',text_id=? WHERE slot=?",(text_id,slot));db.commit()
    db.execute("UPDATE education_campaigns SET status='sending_album',rows_json=? WHERE slot=?",(json.dumps(rows,ensure_ascii=False),slot));db.commit()
    try:ids=send_album(adapter,cfg,rows,text_id)
    except Exception:
        db.execute("UPDATE education_campaigns SET status='unknown',error='Album delivery uncertain; no blind retry' WHERE slot=?",(slot,));db.commit()
        raise RuntimeError('Photo delivery uncertain; not automatically repeated') from None
    db.execute("UPDATE education_campaigns SET status='sent',album_ids=? WHERE slot=?",(json.dumps(ids),slot));db.commit()
    return {'status':'sent','slot':slot,'topic':topic[0],'message_id':text_id,'album_message_ids':ids}


def main():
    args=argparse.ArgumentParser();args.add_argument('--preview',action='store_true');args.add_argument('--status',action='store_true');opt=args.parse_args()
    os.umask(0o077);STATE.mkdir(parents=True,exist_ok=True,mode=0o700)
    logger=logging.getLogger('education');logger.setLevel(logging.INFO)
    logger.addHandler(RotatingFileHandler(STATE/'runner.log',maxBytes=2_000_000,backupCount=3))
    adapter=publisher()
    with (adapter.STATE/'run.lock').open('a') as lock:
        try:fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        except BlockingIOError:return
        with sqlite3.connect(adapter.STATE/'history.sqlite') as db:
            schema(db)
            if opt.status:
                print(json.dumps({'campaigns':db.execute('SELECT slot,status,topic,text_id,album_ids,at FROM education_campaigns ORDER BY slot DESC LIMIT 4').fetchall()},ensure_ascii=False));return
            try:
                cfg=adapter.config('deploy-zibashe-api-1')
                if adapter.CHAT!='-1001283018740' or adapter.CHAT not in cfg.get('Telegram__SilentGroupChatIds','').split(','):raise ValueError('Group destination mismatch')
                result=run(adapter,db,cfg,time.time(),preview=opt.preview)
                logger.info(json.dumps(result,ensure_ascii=False));print(json.dumps(result,ensure_ascii=False))
            except Exception as error:
                logger.error(json.dumps({'status':'failed','type':type(error).__name__}));raise SystemExit('Educational publisher failed; inspect durable status, do not blindly repeat sends') from None


if __name__=='__main__':main()
