# Temporary SMS verification pause

`Onboarding:RequireMainPhoneVerification` is currently `false` in both API and
Portal appsettings. The API is authoritative for onboarding access; the Portal
setting controls the onboarding progress display. Set both to `true` (or override
`Onboarding__RequireMainPhoneVerification=true` in both processes) after SMS
approval, then restart both apps. Missing configuration defaults to requiring
verification. No phone is marked verified by this pause. Email, identity,
contract and payment-number verification requirements are unchanged.

WhatsApp replacement uses the existing pending-number field. The old verified
destination and consent are kept until a valid, unexpired OTP for the new number
is confirmed. Uniqueness is checked at proposal and confirmation, with the
database unique index retained. Cancellation leaves the current destination
and consent unchanged. Existing in-flight WhatsApp codes issued before this
change must be requested again because new OTP hashes are bound to the number.
