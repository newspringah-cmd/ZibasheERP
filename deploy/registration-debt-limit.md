# Item registration debt limit

New customer reservations are blocked when the unpaid balance is **greater than
10,000,000 toman**. Exactly 10,000,000 is allowed. The ledger resolves a known
numeric Telegram ID first, then an unambiguous normalized username for legacy
admin-created identities. It includes nondeleted issued invoices on active
orders across list, inventory and manual sources, less confirmed payments on
those orders. Draft, paid, cancelled and deleted invoices are not debt; pending
payment receipts are not confirmed payments. Unknown new customers start at zero.
Gift registration checks the giver/payer, not the physical recipient.

Customer message:

> به علت بدهی امکان ثبت آیتم ندارید؛ پس از پرداخت با حسابداری هماهنگ کنید لطفاً.

The channel flow checks before starting selection and before confirmation;
`SalesListRequestRepository` also checks inside customer confirmation so an older
pending selection cannot bypass the rule. Existing confirmed reservations and
historical list corrections are not retroactively removed or repriced.

Authorized administrators see a private confirmation with the outstanding amount
and an explicit **ثبت با وجود بدهی** button. This covers interactive item/gift
and next-bottle entries, `/listrequest` and `/nextbottle`, inventory purchases for
customers, initial bottle-owner registration, and an admin's own channel purchase.
No customer may approve an exception. The prompt is tied to actor, exact private
message, original customer and operation; mutable drafts include an item snapshot.
If the draft or debt increases before resumption, the previous approval cannot
silently authorize the changed registration. Consent expires, is single use, and
records a processed `AdminDebtOverride` audit event before resuming the existing
registration flow. Stock, bottle, price, ownership and authorization validations
still run. Failed private delivery leaves the purchase unregistered and asks the
operator to open the private bot chat and retry.

No customer's credit limit is permanently increased. The exception applies only
to the approved registration. Paying down debt restores customer access based
on the live ledger. No invoice, payment, previous item or shipping state is
modified by the policy. No schema migration or new environment setting is needed.

Validation: `ZibasheERP.API.Tests --registration-debt` with isolated
`ZibasheERPCloudDev` SQL, plus the full API and application suites.
