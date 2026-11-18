import type { ReadInvoice } from './invoice-read-contract';

export type Parcel = Readonly<{
  id: string; title: string; registeredOn: string; status: 'preparing' | 'shipped';
  steps: readonly string[]; currentStep: number; note: string;
  items: readonly Readonly<{name: string; volumeMl: string; kind: 'own' | 'gift-received' | 'inventory'}>[];
}>;
export const invoiceLabels = {paid:'پرداخت‌شده',pending:'در انتظار پرداخت'} as const;
export const sourceLabel = (source: ReadInvoice['source']) => source==='inventory'?'🔴 موجودی آماده':'لیست عطر';
