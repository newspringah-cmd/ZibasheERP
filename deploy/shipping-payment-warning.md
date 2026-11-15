# Unpaid-invoice warning during shipping address preparation

The existing authorized shipping operator receives a private Telegram callback
alert when the selected customer has a nondeleted `Issued` invoice attached to
a nondeleted, noncancelled order. The check covers list, inventory and manual
invoices, including invoices for items outside the current ready-to-ship parcel.
It uses the invoice's billed customer ID, not item or gift ownership.

The alert reads:

> ⚠️ فاکتور پرداخت‌نشده باقی مانده است.
> لطفاً به مشتری اطلاع دهید پیش از ارسال پرداخت شود.

The callback is checked at preparation start, customer selection, new/existing
address selection and successful final dispatch. The final check reads the
current database state so a newly issued or newly paid invoice is reflected.
Ambiguous multi-customer groups wait for an explicit customer selection; a single
configured primary customer is selected using the existing shipping rules.

This is an advisory alert for the accountant/manager who clicked. It does not
post debt details in a shared group, send messages to the customer, block address
registration or tracking, change any payment/fulfillment status, or affect the
unidentified legacy manual-address flow. Paid, cancelled, draft and deleted
invoices are excluded. Existing operator authorization remains unchanged.

Validation: `ZibasheERP.API.Tests --shipping-payment-warning` with isolated
`ZibasheERPCloudDev` SQL. No schema migration or environment setting is needed.
