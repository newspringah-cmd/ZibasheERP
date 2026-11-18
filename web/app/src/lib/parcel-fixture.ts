import type { Parcel } from './contracts';

export const parcels = [
  {
    "id": "sample-ready",
    "title": "بستهٔ نمونهٔ آماده‌سازی",
    "registeredOn": "۱۴۰۵/۰۸/۲۶",
    "status": "preparing",
    "steps": [
      "درخواست ارسال ثبت شد",
      "در حال آماده‌سازی",
      "تحویل به پست",
      "کد رهگیری"
    ],
    "currentStep": 1,
    "items": [
      {
        "name": "عطر نمونهٔ گل رز",
        "volumeMl": "۲",
        "kind": "own"
      },
      {
        "name": "عطر نمونهٔ وانیلی",
        "volumeMl": "۱",
        "kind": "gift-received"
      },
      {
        "name": "بستهٔ نمونهٔ آماده",
        "volumeMl": "۱.۵",
        "kind": "inventory"
      }
    ],
    "note": "هدیهٔ دریافتی در بستهٔ گیرنده قرار می‌گیرد. اقلام در انتظار رسیدن به ایران در این بسته نیستند."
  },
  {
    "id": "sample-shipped",
    "title": "بستهٔ نمونهٔ قبلی",
    "registeredOn": "۱۴۰۵/۰۸/۲۰",
    "status": "shipped",
    "steps": [
      "درخواست ارسال ثبت شد",
      "آماده‌سازی انجام شد",
      "تحویل به پست",
      "کد رهگیری ثبت شد"
    ],
    "currentStep": 3,
    "items": [
      {
        "name": "عطر نمونهٔ قبلی",
        "volumeMl": "۳",
        "kind": "own"
      }
    ],
    "note": "این بسته و وضعیت آن ساختگی است؛ کد رهگیری واقعی ندارد."
  }
] as const satisfies readonly Parcel[];
