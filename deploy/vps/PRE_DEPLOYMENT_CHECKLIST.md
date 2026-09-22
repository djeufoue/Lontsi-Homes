# Lontsi Homes Pre-Deployment Checklist

Use this checklist before we start the live VPS deployment and before you submit the live website URL to Stripe.

## 1. Public website pages

Make sure these pages are reachable on the live site:

- `/`
- `/Home/Terms`
- `/Home/Privacy`
- `/Home/RefundPolicy`
- `/Home/Contact`

These pages are important because they make the website look complete and help with provider review.

## 2. Public site details to update

Before going live, supply these values through `PUBLIC_SITE_*` variables in the
ignored `deploy/vps/.env.production` file. For local development, use an ignored
`RentHub.Portal/appsettings.Development.json` file with this section:

```json
"PublicSite": {
  "SiteName": "Lontsi Homes",
  "LegalEntityName": "Lontsi Homes",
  "SupportEmail": "support@example.com",
  "RefundEmail": "refunds@example.com",
  "SupportPhone": "",
  "SupportWhatsApp": "",
  "CompanyAddress": ""
}
```

Keep these values aligned with the public website, Google Workspace aliases, and payment provider verification details.

## 3. Domain plan

Prepare the real domains you want to use:

- main site: `lontsihomes.com`
- API: `api.lontsihomes.com`

You will point both to the VPS public IP during deployment.

## 4. Stripe preparation

Before live approval, make sure you are ready to configure:

- your public live website URL
- your webhook URL
- your real support contact details
- your settlement bank account for card payouts
- your Stripe live publishable key, secret key, and webhook signing secret
- your MTN / Orange admin receiving numbers if needed for later transfer flows

## 5. Application production values

Prepare these production values before deployment:

- JWT signing key
- Azure Blob connection string
- Azure Blob container name
- SMTP host / port / username / password
- Infobip API key / SMS sender / WhatsApp sender / webhook secret
- Stripe publishable key
- Stripe secret key
- Stripe webhook signing secret
- USD to XAF display/conversion rate
- admin seed email / password
- Google Geocoding API key, restricted to the VPS public IP and to the Geocoding API

The geocoding key belongs only in `deploy/vps/.env.production` as `GOOGLE_GEOCODING_API_KEY`; never commit the production value. The browser continues to render OpenStreetMap and does not receive this secret.

For WhatsApp, keep every entry under `Infobip:Templates` disabled until that exact
language variant is active in Meta/Infobip. Then add its provider template ID and set
only that entry's `Approved` value to `true`. Do not enable the accidental Afrikaans
`whatsapp_verification_code_v38` template or a template categorized as Marketing.

Configure Infobip's WhatsApp delivery reports and inbound-message webhook to call:

`https://api.lontsihomes.com/api/webhooks/infobip/whatsapp`

Add the custom header `X-Infobip-Webhook-Secret` with the exact value of
`INFOBIP_WEBHOOK_SECRET`. This is a shared secret, not an HMAC signing configuration.
See [WHATSAPP_PRODUCTION.md](WHATSAPP_PRODUCTION.md) for the existing-VPS update.

## 6. Server target

Current chosen server target:

- `OVHcloud VPS-2`
- `Ubuntu 24.04 LTS`

## 7. First release scope

For the first release, prioritize these working paths:

- landlord registration
- landlord verification
- landlord subscription checkout
- property management workspace
- reminders and operational follow-up

Do not treat tenant rent payment as production-ready yet until the missing payment implementations are finished.
