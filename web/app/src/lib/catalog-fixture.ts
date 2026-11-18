export const rooms = {
 sales:{title:'لیست عطرها',subtitle:'کارت‌های نمونه؛ بدون اتصال به اطلاعات واقعی فروش',icon:'✿',color:'pink',count:'۲'},
 inventory:{title:'موجودی آماده',subtitle:'نمونهٔ بسته‌های تک‌آیتمی و آمادهٔ ارسال',icon:'▣',color:'amber',count:'۱'},
 account:{title:'حساب من',subtitle:'نمای نمونه؛ ورود واقعی مشتری هنوز فعال نیست',icon:'◇',color:'blue',count:''},
 messages:{title:'گفتگو با زیباشی',subtitle:'نمای چیدمان پیام‌ها؛ ارسال و دریافت واقعی غیرفعال است',icon:'☏',color:'mint',count:''},
 staff:{title:'پیشخوان کارکنان',subtitle:'نمای چیدمان؛ ورود کارکنان فعال نیست',icon:'▦',color:'purple',count:''},
} as const;
export type Room = keyof typeof rooms;
export type CatalogItem = Readonly<{id:string;room:'sales'|'inventory';name:string;english:string;code:string;style:string;volume:string;remaining:string;price:string;description:string;minimum:string}>;
export const catalog:readonly CatalogItem[]=[
 {id:'demo-vanilla',room:'sales',name:'عطر نمونهٔ وانیلی',english:'VANILLA · DEMO COLLECTION',code:'نمونه ۰۰۱',style:'',volume:'۱۰۰ میل',remaining:'۱۵ میل',price:'۱۰۰٬۰۰۰',description:'نمونه‌ای از کارت عطر با رایحهٔ گرم و وانیلی؛ برای بررسی شکل نمایش اطلاعات.',minimum:'حداقل ۵ میل'},
 {id:'demo-rose',room:'sales',name:'عطر نمونهٔ گل رز',english:'ROSE · DEMO COLLECTION',code:'نمونه ۰۰۲',style:'rose',volume:'۷۵ میل',remaining:'۴۵ میل',price:'۱۸۰٬۰۰۰',description:'کارت دوم با حال‌وهوای گلی و لطیف؛ تمام اعداد این صفحه ساختگی‌اند.',minimum:'حداقل ۱ میل'},
 {id:'demo-stock',room:'inventory',name:'بستهٔ نمونهٔ آماده',english:'READY TO SHIP · DEMO',code:'موجودی نمونه ۱۰۰۱',style:'fresh',volume:'۱.۵ میل',remaining:'یک بسته',price:'۱۵۰٬۰۰۰',description:'نمونهٔ نمایش موجودی نیم‌میل؛ مبلغ نمایشی عطر با قیمت میلی ۱۰۰ هزار تومان. شیشهٔ نمونه رایگان است.',minimum:'آمادهٔ ارسال'},
];
