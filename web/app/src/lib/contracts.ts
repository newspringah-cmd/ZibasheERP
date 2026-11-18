// Presentation contract only. Authorization and financial calculations belong to the ERP server.
export type InvoiceState = 'paid' | 'pending';
export type Invoice = Readonly<{
  id: string; number: string; issuedOn: string; status: InvoiceState;
  source: 'sales-list' | 'inventory'; total: number; confirmedPaid: number; outstanding: number;
  lines: readonly Readonly<{name: string; volumeMl: string; perfumeAmount: number; bottleAmount: number; bottle: string}>[];
}>;
export type Parcel = Readonly<{
  id: string; title: string; registeredOn: string; status: 'preparing' | 'shipped';
  steps: readonly string[]; currentStep: number; note: string;
  items: readonly Readonly<{name: string; volumeMl: string; kind: 'own' | 'gift-received' | 'inventory'}>[];
}>;
export type AccountSnapshot = Readonly<{
  schemaVersion: 1; synthetic: true; currency: 'IRT'; asOf: string;
  summary: Readonly<{paidInvoiceTotal: number; outstanding: number; confirmedPartialPayments: number}>;
  invoices: readonly Invoice[]; parcels: readonly Parcel[];
}>;
export const money = (value: number) => `${new Intl.NumberFormat('fa-IR').format(value)} تومان`;
export const invoiceLabels = {paid:'پرداخت‌شده',pending:'در انتظار پرداخت'} as const;
export const sourceLabel = (source: Invoice['source']) => source==='inventory'?'🔴 موجودی آماده':'لیست عطر';
