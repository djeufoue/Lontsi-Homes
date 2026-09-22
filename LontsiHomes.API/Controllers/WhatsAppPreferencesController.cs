using System.Data;
using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Helpers;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Services.Messaging;
using LontsiHomes.API.Services.Otp;

namespace LontsiHomes.API.Controllers;

[ApiController]
[Route("api/account/whatsapp")]
[Authorize]
public sealed class WhatsAppPreferencesController : ControllerBase
{
    private const string OtpLoginProvider = "RentHub"; // Persisted Identity token provider; retain for existing accounts.
    private const string OtpHashTokenName = "WhatsAppConsentOtpHashV1";
    private const string OtpExpiryTokenName = "WhatsAppConsentOtpExpiryV1";
    private const string PendingPrimaryTokenName = "WhatsAppPendingUsesPrimaryV1";
    private const string ConsentTextVersion = "whatsapp-transactional-v1";
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IOtpService _otpService;
    private readonly IWhatsAppMessagingService _whatsApp;

    public WhatsAppPreferencesController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IOtpService otpService,
        IWhatsAppMessagingService whatsApp)
    {
        _context = context;
        _userManager = userManager;
        _otpService = otpService;
        _whatsApp = whatsApp;
    }

    [HttpGet]
    public async Task<ActionResult<WhatsAppPreferenceDto>> Get(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Unauthorized();
        }

        return Ok(await BuildStatusAsync(user, cancellationToken));
    }

    [HttpPost("begin-verification")]
    public async Task<ActionResult<WhatsAppPreferenceDto>> BeginVerification(
        [FromBody] BeginWhatsAppVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Unauthorized();
        }

        if (!request.TransactionalConsentAccepted)
        {
            return BadRequest(new { Message = "Vous devez accepter les notifications WhatsApp transactionnelles avant de vérifier le numéro." });
        }

        if (request.UsePrimaryPhoneNumber && string.IsNullOrWhiteSpace(user.PhoneNumber))
        {
            return BadRequest(new { Message = "Ajoutez d’abord un numéro de téléphone principal." });
        }

        var suppliedNumber = request.UsePrimaryPhoneNumber ? user.PhoneNumber : request.PhoneNumber;
        if (!PhoneNumberHelper.TryNormalizeE164(user.CountryCode, suppliedNumber, out var e164))
        {
            return BadRequest(new { Message = "Le numéro WhatsApp n’est pas valide." });
        }

        var hasTransactionalConsent = user.IsWhatsAppPhoneVerified &&
            await _context.UserCommunicationConsents.AnyAsync(consent =>
                consent.UserId == user.Id &&
                consent.Channel == CommunicationChannels.WhatsApp &&
                consent.Purpose == CommunicationPurposes.Transactional &&
                consent.Status == CommunicationConsentStatuses.Granted &&
                consent.PhoneNumberE164 == user.NormalizedWhatsAppPhoneNumber,
                cancellationToken);
        // Compare canonical numbers before altering pending state, generating tokens or contacting Infobip.
        // Re-activation after consent withdrawal still requires a fresh code, even for the same number.
        if (!WhatsAppNumberChange.RequiresVerification(user, e164, hasTransactionalConsent))
        {
            return BadRequest(new
            {
                Code = "whatsapp_number_unchanged",
                Message = "Ce numéro WhatsApp est déjà votre numéro actuel vérifié et actif. Saisissez un autre numéro."
            });
        }

        if (await _context.Users.AnyAsync(candidate =>
                candidate.Id != user.Id &&
                candidate.IsWhatsAppPhoneVerified &&
                candidate.NormalizedWhatsAppPhoneNumber == e164,
                cancellationToken))
        {
            return Conflict(new { Message = "Ce numéro WhatsApp est déjà associé à un autre compte." });
        }

        WhatsAppNumberChange.Propose(user, e164);

        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            return BadRequest(update.Errors);
        }

        var code = _otpService.GenerateCode();
        var hash = _otpService.HashCode(code, CommunicationPurposes.Transactional, WhatsAppNumberChange.OtpSubject(user.Id, e164));
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);
        await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, OtpHashTokenName, hash);
        await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, OtpExpiryTokenName, expiry.ToUnixTimeSeconds().ToString());
        await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, PendingPrimaryTokenName, request.UsePrimaryPhoneNumber.ToString());

        var language = user.EmailLanguage == PlatformLanguage.French ? "fr" : "en_GB";
        var sent = await _whatsApp.SendTemplateAsync(new WhatsAppTemplateMessage(
            e164,
            "whatsapp_verification_code_v1",
            language,
            new[] { code },
            new[] { code }), cancellationToken);
        if (!sent.Succeeded)
        {
            await RemoveOtpAsync(user);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Message = "Le code WhatsApp n’a pas pu être envoyé. Réessayez plus tard.",
                Code = sent.ErrorCode
            });
        }

        return Ok(await BuildStatusAsync(user, cancellationToken));
    }

    [HttpPost("verify")]
    public async Task<ActionResult<WhatsAppPreferenceDto>> Verify(
        [FromBody] VerifyWhatsAppNumberRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(user.PendingWhatsAppPhoneNumber))
        {
            return BadRequest(new { Message = "Aucun numéro WhatsApp n’attend de vérification." });
        }

        var storedHash = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, OtpHashTokenName);
        var storedExpiry = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, OtpExpiryTokenName);
        if (!long.TryParse(storedExpiry, out var expiryUnix) || DateTimeOffset.FromUnixTimeSeconds(expiryUnix) < DateTimeOffset.UtcNow)
        {
            await RemoveOtpAsync(user);
            return BadRequest(new { Message = "Le code de vérification a expiré." });
        }

        if (!_otpService.VerifyCode(request.Code?.Trim() ?? string.Empty, storedHash ?? string.Empty,
                CommunicationPurposes.Transactional, WhatsAppNumberChange.OtpSubject(user.Id, user.PendingWhatsAppPhoneNumber)))
        {
            return BadRequest(new { Message = "Le code de vérification est incorrect." });
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var number = user.PendingWhatsAppPhoneNumber;
        if (await _context.Users.AnyAsync(candidate =>
                candidate.Id != user.Id &&
                candidate.IsWhatsAppPhoneVerified &&
                candidate.NormalizedWhatsAppPhoneNumber == number,
                cancellationToken))
        {
            return Conflict(new { Message = "Ce numéro WhatsApp est déjà associé à un autre compte." });
        }

        var oldConsents = await _context.UserCommunicationConsents
            .Where(consent => consent.UserId == user.Id &&
                              consent.Channel == CommunicationChannels.WhatsApp &&
                              consent.Status == CommunicationConsentStatuses.Granted)
            .ToListAsync(cancellationToken);
        foreach (var consent in oldConsents)
        {
            consent.Status = CommunicationConsentStatuses.Revoked;
            consent.RevokedAt = DateTimeOffset.UtcNow;
        }

        var pendingUsesPrimary = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, PendingPrimaryTokenName);
        var usePrimary = bool.TryParse(pendingUsesPrimary, out var usesPrimary) && usesPrimary;
        // A main-phone edit during verification must not redirect this verified WhatsApp destination.
        usePrimary = usePrimary && PhoneNumberHelper.TryNormalizeE164(user.CountryCode, user.PhoneNumber, out var primary)
            && string.Equals(primary, number, StringComparison.Ordinal);
        WhatsAppNumberChange.Confirm(user, usePrimary, DateTimeOffset.UtcNow);

        foreach (var purpose in new[] { CommunicationPurposes.Authentication, CommunicationPurposes.Transactional })
        {
            _context.UserCommunicationConsents.Add(new UserCommunicationConsent
            {
                UserId = user.Id,
                Channel = CommunicationChannels.WhatsApp,
                Purpose = purpose,
                Status = CommunicationConsentStatuses.Granted,
                PhoneNumberE164 = number,
                TextVersion = ConsentTextVersion,
                Source = "profile",
                GrantedAt = DateTimeOffset.UtcNow,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString()
            });
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new { Message = "Ce numéro WhatsApp est déjà associé à un autre compte." });
        }

        await RemoveOtpAsync(user);
        return Ok(await BuildStatusAsync(user, cancellationToken));
    }

    [HttpPost("cancel-verification")]
    public async Task<ActionResult<WhatsAppPreferenceDto>> CancelVerification(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Unauthorized();

        WhatsAppNumberChange.Cancel(user);
        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded) return BadRequest(update.Errors);
        await RemoveOtpAsync(user);
        return Ok(await BuildStatusAsync(user, cancellationToken));
    }

    [HttpPost("revoke")]
    public async Task<ActionResult<WhatsAppPreferenceDto>> Revoke(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync();
        if (user == null)
        {
            return Unauthorized();
        }

        var consents = await _context.UserCommunicationConsents
            .Where(consent => consent.UserId == user.Id &&
                              consent.Channel == CommunicationChannels.WhatsApp &&
                              consent.Status == CommunicationConsentStatuses.Granted)
            .ToListAsync(cancellationToken);
        foreach (var consent in consents)
        {
            consent.Status = CommunicationConsentStatuses.Revoked;
            consent.RevokedAt = DateTimeOffset.UtcNow;
        }

        user.PendingWhatsAppPhoneNumber = null;
        await _context.SaveChangesAsync(cancellationToken);
        await RemoveOtpAsync(user);
        return Ok(await BuildStatusAsync(user, cancellationToken));
    }

    private async Task<ApplicationUser?> GetCurrentUserAsync()
    {
        var id = UserHelpers.GetUserId(User);
        return string.IsNullOrWhiteSpace(id) ? null : await _userManager.FindByIdAsync(id);
    }

    private async Task<WhatsAppPreferenceDto> BuildStatusAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var active = await _context.UserCommunicationConsents.AnyAsync(consent =>
            consent.UserId == user.Id &&
            consent.Channel == CommunicationChannels.WhatsApp &&
            consent.Purpose == CommunicationPurposes.Transactional &&
            consent.Status == CommunicationConsentStatuses.Granted &&
            consent.PhoneNumberE164 == user.NormalizedWhatsAppPhoneNumber,
            cancellationToken);
        var everRevoked = await _context.UserCommunicationConsents.AnyAsync(consent =>
            consent.UserId == user.Id &&
            consent.Channel == CommunicationChannels.WhatsApp &&
            consent.Status == CommunicationConsentStatuses.Revoked,
            cancellationToken);
        var pendingOtpHash = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, OtpHashTokenName);

        var state = !string.IsNullOrWhiteSpace(user.PendingWhatsAppPhoneNumber)
            ? string.IsNullOrWhiteSpace(pendingOtpHash) ? "Numéro proposé" : "Vérification en attente"
            : active && user.IsWhatsAppPhoneVerified
                ? "Actif"
                : !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) && everRevoked
                    ? "Consentement retiré"
                    : "Non activé";

        return new WhatsAppPreferenceDto(
            state,
            user.WhatsAppPhoneNumber,
            user.PendingWhatsAppPhoneNumber,
            user.UsePrimaryPhoneForWhatsApp,
            user.IsWhatsAppPhoneVerified,
            active,
            ConsentTextVersion);
    }

    private async Task RemoveOtpAsync(ApplicationUser user)
    {
        await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, OtpHashTokenName);
        await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, OtpExpiryTokenName);
        await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, PendingPrimaryTokenName);
    }
}
