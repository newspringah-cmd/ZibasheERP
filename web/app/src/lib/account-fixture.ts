import type { AccountSnapshot } from "./contracts";

// Synthetic only; never substitute an ERP response without a server-side identity boundary.
export const account = {
  "schemaVersion": 1,
  "synthetic": true,
  "currency": "IRT",
  "asOf": "۱۴۰۵/۰۸/۲۷ · نمونه",
  "summary": {
    "paidInvoiceTotal": 380000,
    "outstanding": 630000,
    "confirmedPartialPayments": 50000
  },
  "invoices": [
    {
      "id": "sample-paid",
      "number": "DEMO-001",
      "issuedOn": "۱۴۰۵/۰۸/۲۵",
      "status": "paid",
      "source": "sales-list",
      "total": 380000,
      "confirmedPaid": 380000,
      "outstanding": 0,
      "lines": [
        {
          "name": "عطر نمونهٔ گل رز",
          "volumeMl": "۲",
          "perfumeAmount": 360000,
          "bottleAmount": 20000,
          "bottle": "نرمال ۲ میل"
        }
      ]
    },
    {
      "id": "sample-inventory",
      "number": "DEMO-002",
      "issuedOn": "۱۴۰۵/۰۸/۲۶",
      "status": "pending",
      "source": "inventory",
      "total": 150000,
      "confirmedPaid": 50000,
      "outstanding": 100000,
      "lines": [
        {
          "name": "بستهٔ نمونهٔ آماده",
          "volumeMl": "۱.۵",
          "perfumeAmount": 150000,
          "bottleAmount": 0,
          "bottle": "باتل اصلی رایگان"
        }
      ]
    },
    {
      "id": "sample-waiting",
      "number": "DEMO-003",
      "issuedOn": "۱۴۰۵/۰۸/۲۷",
      "status": "pending",
      "source": "sales-list",
      "total": 530000,
      "confirmedPaid": 0,
      "outstanding": 530000,
      "lines": [
        {
          "name": "عطر نمونهٔ وانیلی",
          "volumeMl": "۵",
          "perfumeAmount": 500000,
          "bottleAmount": 30000,
          "bottle": "فانتزی ۵ میل"
        }
      ]
    }
  ],
  "parcels": [
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
  ]
} as const satisfies AccountSnapshot;
