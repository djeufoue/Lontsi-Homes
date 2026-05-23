# RentHub Pre-Deployment Checklist

Use this checklist before we start the live VPS deployment and before you submit the live website URL to Notch Pay.

## 1. Public website pages

Make sure these pages are reachable on the live site:

- `/`
- `/Home/Terms`
- `/Home/Privacy`
- `/Home/RefundPolicy`
- `/Home/Contact`

These pages are important because they make the website look complete and help with provider review.

## 2. Public site details to update

Before going live, update these values in:

- `C:\Projects\Rent Management Project\RentHub\RentHub.Portal\appsettings.json`

Section:

```json
"PublicSite": {
  "SiteName": "RentHub",
  "LegalEntityName": "RentHub",
  "SupportEmail": "REMOVED_PRIVATE_VALUE",
  "SupportPhone": "+237 600 000 000",
  "SupportWhatsApp": "+237 600 000 000",
  "CompanyAddress": "Douala, Cameroon"
}
```

Replace them with your real support and business details.

## 3. Domain plan

Prepare the real domains you want to use:

- main site: `renthub...`
- API: `api.renthub...`

You will point both to the VPS public IP during deployment.

## 4. Notch Pay preparation

Before live approval, make sure you are ready to configure:

- your public live website URL
- your webhook URL
- your real support contact details
- your settlement bank account for card payouts
- your MTN / Orange admin receiving numbers if needed for later transfer flows

## 5. Application production values

Prepare these production values before deployment:

- JWT signing key
- Azure Blob connection string
- Azure Blob container name
- SMTP host / port / username / password
- Twilio Account SID / Auth Token / sender numbers
- Notch Pay API key
- Notch Pay webhook secret
- admin seed email / password

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
