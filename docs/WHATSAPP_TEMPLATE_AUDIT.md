# WhatsApp template audit — supplied Infobip payload screenshots

## Status: variable mappings and receipt transport implemented; public URL/button configuration pending

The transport and persisted outbox now support a `DOCUMENT` header (`mediaUrl`,
`filename`). Body-only templates omit the buttons/header properties entirely.
Legacy outbox string arrays remain readable; no database migration is required.
The registry now validates the shapes demonstrated by the screenshots below.

| Template | Captured language | Body variables | URL buttons | Header |
| --- | --- | ---: | ---: | --- |
| rent_payment_receipt | fr + en | 7 | 0 | DOCUMENT |
| landlord_rent_payment_received | fr | 5 | 1 | None |
| rent_due_soon | en | 6 | 1 | None |
| rent_due_today | fr | 6 | 1 | None |
| rent_overdue | fr | 6 | 1 | None |
| tenancy_ending_soon | en | 4 | 1 | None |
| tenancy_request_received | en | 4 | 0 | None |
| tenancy_request_status_update | en | 5 | 0 | None |
| whatsapp_verification_code_v1 | fr | 1 | 1 (OTP value) | None |

All nine application events are represented. The extra Afrikaans template from
the provider listing is not used by the application. Opposite-language payload
shapes (except the bilingual receipt capture) have not been independently
verified. Following the user's instruction, both languages now share the
shown variable order and component counts. Recheck if a provider translation changes.

## Implemented variable order

- Receipt: tenant, property, apartment, amount, currency, payment date, receipt number.
- Upcoming/today reminders: tenant, property, apartment, due date, balance, currency.
- Overdue reminder: tenant, property, apartment, outstanding period(s), balance, currency.
- Landlord receipt: landlord, amount including currency, tenant, property/apartment, paid period.
- Ending tenancy: tenant, property, apartment, end date.
- Request received: recipient, requester, localized request type, property/apartment.
- Request status: recipient, localized request type, requester, property/apartment, localized status.
- OTP: verification code in body and copy-code button (unchanged).

Dates and monetary values use the recipient's communications language. The request
templates' visible fixed buttons require no dynamic button parameters in their supplied JSON.

## Required before completing the integration

1. Set `Infobip:ReceiptMediaBaseUrl` to the public HTTPS **API** base URL (not the
   portal or Infobip API). Environment key: `Infobip__ReceiptMediaBaseUrl`.
   Development is now configured with the currently running ngrok tunnel,
   verified through its local inspector to target `https://localhost:64583`.
   Keep that tunnel running and update this setting if its public address changes.
   The production/default value remains empty intentionally.
   The new `/api/receipts/whatsapp.pdf?token=...` endpoint returns only the PDF
   authorized by its protected token, bound to the tenant, successful payment,
   verification code and language. Tokens last 24 hours and are created at each
   delivery attempt, not at enqueue time. Existing authenticated receipt routes
   are unchanged. The existing PDF builder/QR footer are reused.
   An unset URL produces `receipt_media_not_configured`, not an HTTP send.
   Persist/share ASP.NET Data Protection keys across restarts/replicas in production.
   Treat signed URLs as secrets: exclude/redact query strings in proxy/access logs.
2. Supply the configured button URLs before filling `Infobip:UrlButtonParameters`.
   These settings are the **dynamic suffix** only. Supported substitution is
   `{verificationCode}` for `landlord_rent_payment_received`, and `{tenancyId}`
   for `rent_due_soon`, `rent_due_today`, `rent_overdue`, `tenancy_ending_soon`.
   For example, `{tenancyId}` is appropriate only if the approved prefix already
   includes the full path/query before that ID. No suffix is assumed or enabled.
   Missing settings produce `template_url_button_not_configured` and skip sending.
3. The provider list classifies `tenancy_request_received:fr` as Marketing, while
   the application treats it as Utility. Confirm/correct this with the provider
   before using it under transactional consent; this patch does not change
   consent or provider approval/category settings.

Transport and controller tests use fictional values and mocked services, not real
recipients or payments. Tests cover bilingual value order, token tampering/expiry,
wrong tenant/status/revoked/deleted receipt, PDF response and legacy queued arrays.
Passing them does not confirm public URL reachability or delivery to WhatsApp.
Previously skipped deliveries are not automatically replayed; this avoids duplicates.

Reference: https://www.infobip.com/docs/tutorials/send-whatsapp-template-messages
requires values in template order and media URLs for media headers.
