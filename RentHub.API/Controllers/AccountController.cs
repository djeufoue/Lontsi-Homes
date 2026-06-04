using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Auth;
using RentHub.API.Services.Email;
using RentHub.API.Services.Users;
using RentHub.API.Services.Kyc;
using System.Security.Claims;
using System.Linq;

using RentHub.API.Helpers;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AccountController : ControllerBase
    {
        private const string OtpLoginProvider = "RentHub";
        private const string ActivationOtpTokenName = "ActivationOtpCode";
        private const string ActivationOtpExpiryTokenName = "ActivationOtpExpiryUnix";
        private const string PhoneOtpTokenName = "PhoneOtpCode";
        private const string PhoneOtpExpiryTokenName = "PhoneOtpExpiryUnix";
        private const string SubscriptionPaymentOtpTokenName = "SubscriptionPaymentOtpCode";
        private const string SubscriptionPaymentOtpExpiryTokenName = "SubscriptionPaymentOtpExpiryUnix";
        private const string PayoutOtpTokenName = "PayoutOtpCode";
        private const string PayoutOtpExpiryTokenName = "PayoutOtpExpiryUnix";
        private const string WhatsAppOtpTokenName = "WhatsAppOtpCode";
        private const string WhatsAppOtpExpiryTokenName = "WhatsAppOtpExpiryUnix";
        private const string PlatformTermsVersion = "2026-06-kyc-v1";

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly TokenService _tokenService;
        private readonly IEmailService _emailService;
        private readonly IUserOnboardingService _userOnboardingService;
        private readonly IKycFileStorageService _kycFileStorageService;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager,
            ApplicationDbContext context,
            TokenService tokenService,
            IEmailService emailService,
            IUserOnboardingService userOnboardingService,
            IKycFileStorageService kycFileStorageService,
            ILogger<AccountController> logger)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _tokenService = tokenService;
            _emailService = emailService;
            _userOnboardingService = userOnboardingService;
            _kycFileStorageService = kycFileStorageService;
            _logger = logger;
        }

        /// <summary>
        /// Registers a new user. All self-registered accounts are landlords and must verify
        /// their email with an OTP before login is allowed.
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            ApplicationUser? createdUser = null;

            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var existingUser = await _userManager.FindByEmailAsync(request.Email);
                if (existingUser != null)
                {
                    return BadRequest("An account with this email already exists. Please log in instead.");
                }

                SubscriptionPlan? plan = null;
                if (request.PlanId.HasValue)
                {
                    plan = await _context.SubscriptionPlans.FindAsync(request.PlanId.Value);
                    if (plan == null)
                    {
                        return BadRequest("Invalid subscription plan.");
                    }
                }

                if (request.PayoutChannel is not PayoutChannelEnum.MtnMoney and not PayoutChannelEnum.OrangeMoney)
                {
                    return BadRequest("Choose MTN Money or Orange Money for payout payments.");
                }

                var payoutValidation = ValidateCameroonMobileMoneyNumber(
                    request.PayoutPhoneNumber,
                    request.PayoutChannel,
                    "payout number",
                    out var normalizedPayoutPhone);
                if (payoutValidation != null)
                {
                    return payoutValidation;
                }

                var user = new ApplicationUser
                {
                    UserName = request.Email,
                    Email = request.Email,
                    FullName = string.Join(" ", new[] { request.FirstName?.Trim(), request.LastName?.Trim() }.Where(v => !string.IsNullOrWhiteSpace(v))),
                    CountryCode = request.CountryCode?.Trim(),
                    PhoneNumber = request.PhoneNumber?.Trim(),
                    UsePrimaryPhoneForSubscriptionPayments = false,
                    SubscriptionPaymentPhoneNumber = normalizedPayoutPhone,
                    SubscriptionPaymentChannel = request.PayoutChannel,
                    PayoutPhoneNumber = normalizedPayoutPhone,
                    PayoutChannel = request.PayoutChannel,
                    WhatsAppPhoneNumber = request.WhatsAppPhoneNumber.Trim(),
                    EmailConfirmed = false
                };

                var result = await _userManager.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                createdUser = user;

                var roleResult = await _userManager.AddToRoleAsync(user, "Landlord");
                if (!roleResult.Succeeded)
                {
                    await _userManager.DeleteAsync(user);
                    createdUser = null;
                    return BadRequest(roleResult.Errors);
                }

                if (plan != null)
                {
                    var subscription = new UserSubscription
                    {
                        UserId = user.Id,
                        SubscriptionPlanId = plan.Id,
                        StartDate = DateTimeOffset.UtcNow,
                        EndDate = DateTimeOffset.UtcNow.AddDays(plan.DurationInDays),
                        PlanNameSnapshot = plan.Name,
                        PlanPriceSnapshot = plan.Price,
                        PlanDurationInDaysSnapshot = plan.DurationInDays,
                        PlanMaxPropertiesSnapshot = plan.MaxProperties,
                        PlanMaxApartmentsPerPropertySnapshot = plan.MaxApartmentsPerProperty,
                        PaymentStatus = PaymentStatusEnum.Pending,
                        IsApproved = false,
                        CreatedBy = user.Id,
                        CreatedAt = DateTimeOffset.UtcNow,
                        IsDeleted = false
                    };

                    _context.UserSubscriptions.Add(subscription);
                }

                await _context.SaveChangesAsync();

                await _userOnboardingService.SendActivationOtpAsync(user);

                return Ok(new
                {
                    RequiresActivation = true,
                    Email = user.Email,
                    Message = "Registration successful. Check your email, payout number, and WhatsApp for OTP codes to activate your account."
                });
            }
            catch (OtpSendThrottledException ex)
            {
                await TryRollbackRegistrationAsync(createdUser);
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                await TryRollbackRegistrationAsync(createdUser);

                _logger.LogError(ex,
                    "Registration failed for email {Email} with plan {PlanId}",
                    request.Email,
                    request.PlanId);

                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "REGISTRATION_FAILED",
                    Message = "We could not complete account creation right now. Please try again in a few minutes."
                });
            }
        }

        [HttpPost("landlord-registration/start")]
        public async Task<IActionResult> StartLandlordRegistration([FromBody] StartLandlordRegistrationRequest request)
        {
            ApplicationUser? createdUser = null;

            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var normalizedEmail = request.Email.Trim();
                var existingUser = await _userManager.FindByEmailAsync(normalizedEmail);
                if (existingUser != null)
                {
                    var existingRoles = await _userManager.GetRolesAsync(existingUser);
                    if (existingRoles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase)))
                    {
                        var existingStatus = await BuildLandlordOnboardingStatusAsync(existingUser);
                        return Ok(new
                        {
                            RequiresActivation = !existingStatus.IsComplete,
                            Email = existingUser.Email,
                            NextStep = existingStatus.NextStep,
                            Message = existingStatus.IsComplete
                                ? "This landlord account is already ready. Please sign in."
                                : "This account already exists. Continue the registration where you stopped."
                        });
                    }

                    return BadRequest("An account with this email already exists. Please log in instead.");
                }

                SubscriptionPlan? plan = null;
                if (request.PlanId.HasValue)
                {
                    plan = await _context.SubscriptionPlans.FindAsync(request.PlanId.Value);
                    if (plan == null)
                    {
                        return BadRequest("Invalid subscription plan.");
                    }
                }

                var user = new ApplicationUser
                {
                    UserName = normalizedEmail,
                    Email = normalizedEmail,
                    FullName = string.Join(" ", new[] { request.FirstName?.Trim(), request.LastName?.Trim() }.Where(v => !string.IsNullOrWhiteSpace(v))),
                    CountryCode = request.CountryCode?.Trim(),
                    EmailConfirmed = false,
                    PhoneNumberConfirmed = false,
                    UsePrimaryPhoneForSubscriptionPayments = true,
                    UsePrimaryPhoneForRentPayouts = true,
                    IsSubscriptionPaymentPhoneVerified = false,
                    IsPayoutPhoneVerified = false,
                    IsWhatsAppPhoneVerified = false
                };

                var result = await _userManager.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                createdUser = user;

                var roleResult = await _userManager.AddToRoleAsync(user, "Landlord");
                if (!roleResult.Succeeded)
                {
                    await _userManager.DeleteAsync(user);
                    createdUser = null;
                    return BadRequest(roleResult.Errors);
                }

                if (plan != null)
                {
                    _context.UserSubscriptions.Add(new UserSubscription
                    {
                        UserId = user.Id,
                        SubscriptionPlanId = plan.Id,
                        StartDate = DateTimeOffset.UtcNow,
                        EndDate = DateTimeOffset.UtcNow.AddDays(plan.DurationInDays),
                        PlanNameSnapshot = plan.Name,
                        PlanPriceSnapshot = plan.Price,
                        PlanDurationInDaysSnapshot = plan.DurationInDays,
                        PlanMaxPropertiesSnapshot = plan.MaxProperties,
                        PlanMaxApartmentsPerPropertySnapshot = plan.MaxApartmentsPerProperty,
                        PaymentStatus = PaymentStatusEnum.Pending,
                        IsApproved = false,
                        CreatedBy = user.Id,
                        CreatedAt = DateTimeOffset.UtcNow,
                        IsDeleted = false
                    });
                }

                await _context.SaveChangesAsync();
                await _userOnboardingService.SendLandlordEmailOtpAsync(user);

                var status = await BuildLandlordOnboardingStatusAsync(user);
                return Ok(new
                {
                    RequiresActivation = true,
                    Email = user.Email,
                    NextStep = status.NextStep,
                    Status = status,
                    Message = "Account created. Check your email for the OTP code to continue registration."
                });
            }
            catch (OtpSendThrottledException ex)
            {
                await TryRollbackRegistrationAsync(createdUser);
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                await TryRollbackRegistrationAsync(createdUser);

                _logger.LogError(ex,
                    "Step registration failed for email {Email} with plan {PlanId}",
                    request.Email,
                    request.PlanId);

                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "REGISTRATION_FAILED",
                    Message = "We could not complete account creation right now. Please try again in a few minutes."
                });
            }
        }

        [HttpGet("landlord-registration/status")]
        public async Task<IActionResult> GetLandlordRegistrationStatus([FromQuery] string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return BadRequest("Email is required.");
            }

            var user = await _userManager.FindByEmailAsync(email.Trim());
            if (user == null || !await _userManager.IsInRoleAsync(user, "Landlord"))
            {
                return NotFound("Landlord account was not found.");
            }

            var status = await BuildLandlordOnboardingStatusAsync(user);
            return Ok(SanitizeOnboardingStatusForAnonymous(status));
        }

        [HttpPost("landlord-registration/verify-email")]
        public async Task<IActionResult> VerifyLandlordEmail([FromBody] VerifyEmailOtpRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                if (!user.EmailConfirmed)
                {
                    var otpResult = await ValidateOtpAsync(
                        user,
                        ActivationOtpTokenName,
                        ActivationOtpExpiryTokenName,
                        request.EmailOtp.Trim(),
                        "email");

                    if (otpResult != null)
                    {
                        return otpResult;
                    }

                    user.EmailConfirmed = true;
                    var updateResult = await _userManager.UpdateAsync(user);
                    if (!updateResult.Succeeded)
                    {
                        return BadRequest(updateResult.Errors);
                    }

                    await RemoveOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName);
                }

                var status = await BuildLandlordOnboardingStatusAsync(user);
                return Ok(new
                {
                    Email = user.Email,
                    NextStep = status.NextStep,
                    Status = status,
                    Message = "Email verified. Continue with phone verification."
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "VerifyLandlordEmail", "Unable to verify email OTP right now. Please try again.");
            }
        }

        [HttpPost("landlord-registration/phone")]
        public async Task<IActionResult> UpsertLandlordPhone([FromBody] UpsertLandlordPhoneRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                if (!user.EmailConfirmed)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "LANDLORD_ONBOARDING_INCOMPLETE",
                        Email = user.Email,
                        NextStep = LandlordOnboardingSteps.Email,
                        Message = "Confirm your email before verifying your phone number."
                    });
                }

                var countryCode = NormalizeCountryCode(request.CountryCode);
                if (!string.Equals(countryCode, "+237", StringComparison.Ordinal))
                {
                    return BadRequest(new
                    {
                        Code = "PHONE_COUNTRY_UNSUPPORTED",
                        Message = "Only Cameroon country code +237 is supported for landlord phone verification right now."
                    });
                }

                if (!TryNormalizeLocalCameroonPhoneNumber(request.PhoneNumber, out var phone))
                {
                    return BadRequest(new
                    {
                        Code = "PHONE_NUMBER_INVALID",
                        Message = "Enter the 9-digit Cameroon phone number without the country code. Example: REMOVED_PRIVATE_VALUE."
                    });
                }

                var phoneChanged = !SamePhone(user.PhoneNumber, phone);

                user.CountryCode = countryCode;
                user.PhoneNumber = phone;

                if (phoneChanged)
                {
                    user.PhoneNumberConfirmed = false;

                    if (user.UsePrimaryPhoneForSubscriptionPayments)
                    {
                        user.SubscriptionPaymentPhoneNumber = phone;
                        user.IsSubscriptionPaymentPhoneVerified = false;
                        user.SubscriptionPaymentPhoneVerifiedAt = null;
                    }

                    if (user.UsePrimaryPhoneForRentPayouts)
                    {
                        user.PayoutPhoneNumber = phone;
                        user.IsPayoutPhoneVerified = false;
                        user.PayoutPhoneVerifiedAt = null;
                    }
                }

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                if (!user.PhoneNumberConfirmed)
                {
                    await _userOnboardingService.SendLandlordPhoneOtpAsync(user);
                }

                var status = await BuildLandlordOnboardingStatusAsync(user);
                return Ok(new
                {
                    Email = user.Email,
                    NextStep = status.NextStep,
                    Status = status,
                    Message = user.PhoneNumberConfirmed
                        ? "Phone number is already verified."
                        : "Phone OTP sent. Enter it to continue."
                });
            }
            catch (OtpSendThrottledException ex)
            {
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                return ServerError(ex, "UpsertLandlordPhone", "Unable to save the phone number right now. Please try again.");
            }
        }

        [HttpPost("landlord-registration/verify-phone")]
        public async Task<IActionResult> VerifyLandlordPhone([FromBody] VerifyPhoneOtpRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                if (string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    return BadRequest("Add a phone number before verifying it.");
                }

                if (!user.PhoneNumberConfirmed)
                {
                    var otpResult = await ValidateOtpAsync(
                        user,
                        PhoneOtpTokenName,
                        PhoneOtpExpiryTokenName,
                        request.PhoneOtp.Trim(),
                        "phone number");

                    if (otpResult != null)
                    {
                        return otpResult;
                    }

                    user.PhoneNumberConfirmed = true;

                    if (user.UsePrimaryPhoneForSubscriptionPayments && SamePhone(user.SubscriptionPaymentPhoneNumber, user.PhoneNumber))
                    {
                        user.IsSubscriptionPaymentPhoneVerified = true;
                        user.SubscriptionPaymentPhoneVerifiedAt = DateTimeOffset.UtcNow;
                    }

                    if (user.UsePrimaryPhoneForRentPayouts && SamePhone(user.PayoutPhoneNumber, user.PhoneNumber))
                    {
                        user.IsPayoutPhoneVerified = true;
                        user.PayoutPhoneVerifiedAt = DateTimeOffset.UtcNow;
                    }

                    var updateResult = await _userManager.UpdateAsync(user);
                    if (!updateResult.Succeeded)
                    {
                        return BadRequest(updateResult.Errors);
                    }

                    await RemoveOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName);
                }

                var status = await BuildLandlordOnboardingStatusAsync(user);
                return Ok(new
                {
                    Email = user.Email,
                    NextStep = status.NextStep,
                    Status = status,
                    Message = "Phone number verified. Configure mobile payment numbers next."
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "VerifyLandlordPhone", "Unable to verify phone OTP right now. Please try again.");
            }
        }

        [HttpPost("landlord-registration/mobile-payments")]
        public async Task<IActionResult> UpsertLandlordMobilePayments([FromBody] UpsertLandlordMobilePaymentsRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                if (!user.EmailConfirmed || !user.PhoneNumberConfirmed || string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "LANDLORD_ONBOARDING_INCOMPLETE",
                        Email = user.Email,
                        NextStep = !user.EmailConfirmed ? LandlordOnboardingSteps.Email : LandlordOnboardingSteps.Phone,
                        Message = "Confirm your email and main phone number before configuring mobile payments."
                    });
                }

                if (request.SubscriptionPaymentChannel is not PayoutChannelEnum.MtnMoney and not PayoutChannelEnum.OrangeMoney)
                {
                    return BadRequest("Choose MTN Money or Orange Money for subscription payments.");
                }

                if (request.PayoutChannel is not PayoutChannelEnum.MtnMoney and not PayoutChannelEnum.OrangeMoney)
                {
                    return BadRequest("Choose MTN Money or Orange Money for rent payouts.");
                }

                var subscriptionPhone = request.UsePrimaryPhoneForSubscriptionPayments
                    ? user.PhoneNumber
                    : request.SubscriptionPaymentPhoneNumber?.Trim();

                if (string.IsNullOrWhiteSpace(subscriptionPhone))
                {
                    return BadRequest("Subscription payment number is required.");
                }

                var payoutPhone = request.UsePrimaryPhoneForRentPayouts
                    ? user.PhoneNumber
                    : request.PayoutPhoneNumber?.Trim();

                if (string.IsNullOrWhiteSpace(payoutPhone))
                {
                    return BadRequest("Rent payout number is required.");
                }

                var subscriptionValidation = ValidateCameroonMobileMoneyNumber(
                    subscriptionPhone,
                    request.SubscriptionPaymentChannel,
                    "subscription payment number",
                    out var normalizedSubscriptionPhone);
                if (subscriptionValidation != null)
                {
                    return subscriptionValidation;
                }

                var payoutValidation = ValidateCameroonMobileMoneyNumber(
                    payoutPhone,
                    request.PayoutChannel,
                    "rent payout number",
                    out var normalizedPayoutPhone);
                if (payoutValidation != null)
                {
                    return payoutValidation;
                }

                var oldSubscriptionPhone = user.SubscriptionPaymentPhoneNumber;
                var oldSubscriptionVerified = user.IsSubscriptionPaymentPhoneVerified;
                var oldPayoutPhone = user.PayoutPhoneNumber;
                var oldPayoutVerified = user.IsPayoutPhoneVerified;
                var oldWhatsAppPhone = user.WhatsAppPhoneNumber;
                var oldWhatsAppVerified = user.IsWhatsAppPhoneVerified;

                user.UsePrimaryPhoneForSubscriptionPayments = request.UsePrimaryPhoneForSubscriptionPayments;
                user.SubscriptionPaymentPhoneNumber = normalizedSubscriptionPhone;
                user.SubscriptionPaymentChannel = request.SubscriptionPaymentChannel;
                user.UsePrimaryPhoneForRentPayouts = request.UsePrimaryPhoneForRentPayouts;
                user.PayoutPhoneNumber = normalizedPayoutPhone;
                user.PayoutChannel = request.PayoutChannel;
                user.WhatsAppPhoneNumber = string.IsNullOrWhiteSpace(request.WhatsAppPhoneNumber)
                    ? null
                    : request.WhatsAppPhoneNumber.Trim();

                user.IsSubscriptionPaymentPhoneVerified =
                    (oldSubscriptionVerified && SamePhone(oldSubscriptionPhone, user.SubscriptionPaymentPhoneNumber)) ||
                    SamePhone(user.SubscriptionPaymentPhoneNumber, user.PhoneNumber) && user.PhoneNumberConfirmed;
                user.SubscriptionPaymentPhoneVerifiedAt = user.IsSubscriptionPaymentPhoneVerified
                    ? user.SubscriptionPaymentPhoneVerifiedAt ?? DateTimeOffset.UtcNow
                    : null;

                user.IsPayoutPhoneVerified =
                    (oldPayoutVerified && SamePhone(oldPayoutPhone, user.PayoutPhoneNumber)) ||
                    SamePhone(user.PayoutPhoneNumber, user.PhoneNumber) && user.PhoneNumberConfirmed ||
                    SamePhone(user.PayoutPhoneNumber, user.SubscriptionPaymentPhoneNumber) && user.IsSubscriptionPaymentPhoneVerified;
                user.PayoutPhoneVerifiedAt = user.IsPayoutPhoneVerified
                    ? user.PayoutPhoneVerifiedAt ?? DateTimeOffset.UtcNow
                    : null;

                user.IsWhatsAppPhoneVerified =
                    string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) ||
                    (oldWhatsAppVerified && SamePhone(oldWhatsAppPhone, user.WhatsAppPhoneNumber)) ||
                    SamePhone(user.WhatsAppPhoneNumber, user.PhoneNumber) && user.PhoneNumberConfirmed;
                user.WhatsAppPhoneVerifiedAt = user.IsWhatsAppPhoneVerified && !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber)
                    ? user.WhatsAppPhoneVerifiedAt ?? DateTimeOffset.UtcNow
                    : null;

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                var sendSubscriptionOtp = !user.IsSubscriptionPaymentPhoneVerified;
                var sendPayoutOtp = !user.IsPayoutPhoneVerified && !SamePhone(user.PayoutPhoneNumber, user.SubscriptionPaymentPhoneNumber);
                var sendWhatsAppOtp = !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) && !user.IsWhatsAppPhoneVerified;

                if (sendSubscriptionOtp || sendPayoutOtp || sendWhatsAppOtp)
                {
                    await _userOnboardingService.SendLandlordMobilePaymentOtpsAsync(
                        user,
                        sendSubscriptionOtp,
                        sendPayoutOtp,
                        sendWhatsAppOtp);
                }

                var status = await BuildLandlordOnboardingStatusAsync(user);
                return Ok(new
                {
                    Email = user.Email,
                    NextStep = status.NextStep,
                    Status = status,
                    Message = status.IsComplete
                        ? "Mobile payment information is already verified."
                        : "OTP codes were sent only to mobile numbers that still need verification."
                });
            }
            catch (OtpSendThrottledException ex)
            {
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                return ServerError(ex, "UpsertLandlordMobilePayments", "Unable to save mobile payment details right now. Please try again.");
            }
        }

        [HttpPost("landlord-registration/verify-mobile-payments")]
        public async Task<IActionResult> VerifyLandlordMobilePayments([FromBody] VerifyMobilePaymentPhonesRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                if (string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber) || user.SubscriptionPaymentChannel == null)
                {
                    return BadRequest("Configure subscription payment details before verifying them.");
                }

                if (string.IsNullOrWhiteSpace(user.PayoutPhoneNumber) || user.PayoutChannel == null)
                {
                    return BadRequest("Configure rent payout details before verifying them.");
                }

                var verifiedAt = DateTimeOffset.UtcNow;

                if (!user.IsSubscriptionPaymentPhoneVerified)
                {
                    if (SamePhone(user.SubscriptionPaymentPhoneNumber, user.PhoneNumber) && user.PhoneNumberConfirmed)
                    {
                        user.IsSubscriptionPaymentPhoneVerified = true;
                        user.SubscriptionPaymentPhoneVerifiedAt = verifiedAt;
                    }
                    else
                    {
                        var otpResult = await ValidateOtpAsync(
                            user,
                            SubscriptionPaymentOtpTokenName,
                            SubscriptionPaymentOtpExpiryTokenName,
                            request.SubscriptionPaymentOtp?.Trim() ?? string.Empty,
                            "subscription payment number");

                        if (otpResult != null)
                        {
                            return otpResult;
                        }

                        user.IsSubscriptionPaymentPhoneVerified = true;
                        user.SubscriptionPaymentPhoneVerifiedAt = verifiedAt;
                    }
                }

                if (!user.IsPayoutPhoneVerified)
                {
                    if (SamePhone(user.PayoutPhoneNumber, user.PhoneNumber) && user.PhoneNumberConfirmed ||
                        SamePhone(user.PayoutPhoneNumber, user.SubscriptionPaymentPhoneNumber) && user.IsSubscriptionPaymentPhoneVerified)
                    {
                        user.IsPayoutPhoneVerified = true;
                        user.PayoutPhoneVerifiedAt = verifiedAt;
                    }
                    else
                    {
                        var otpResult = await ValidateOtpAsync(
                            user,
                            PayoutOtpTokenName,
                            PayoutOtpExpiryTokenName,
                            request.PayoutOtp?.Trim() ?? string.Empty,
                            "rent payout number");

                        if (otpResult != null)
                        {
                            return otpResult;
                        }

                        user.IsPayoutPhoneVerified = true;
                        user.PayoutPhoneVerifiedAt = verifiedAt;
                    }
                }

                if (!string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) && !user.IsWhatsAppPhoneVerified)
                {
                    if (SamePhone(user.WhatsAppPhoneNumber, user.PhoneNumber) && user.PhoneNumberConfirmed)
                    {
                        user.IsWhatsAppPhoneVerified = true;
                        user.WhatsAppPhoneVerifiedAt = verifiedAt;
                    }
                    else
                    {
                        var otpResult = await ValidateOtpAsync(
                            user,
                            WhatsAppOtpTokenName,
                            WhatsAppOtpExpiryTokenName,
                            request.WhatsAppOtp?.Trim() ?? string.Empty,
                            "WhatsApp");

                        if (otpResult != null)
                        {
                            return otpResult;
                        }

                        user.IsWhatsAppPhoneVerified = true;
                        user.WhatsAppPhoneVerifiedAt = verifiedAt;
                    }
                }

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                if (user.IsSubscriptionPaymentPhoneVerified)
                {
                    await RemoveOtpAsync(user, SubscriptionPaymentOtpTokenName, SubscriptionPaymentOtpExpiryTokenName);
                }

                if (user.IsPayoutPhoneVerified)
                {
                    await RemoveOtpAsync(user, PayoutOtpTokenName, PayoutOtpExpiryTokenName);
                }

                if (user.IsWhatsAppPhoneVerified)
                {
                    await RemoveOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName);
                }

                var status = await BuildLandlordOnboardingStatusAsync(user);
                if (status.IsComplete)
                {
                    var token = await _tokenService.GenerateTokenAsync(user);
                    return Ok(new
                    {
                        Email = user.Email,
                        NextStep = status.NextStep,
                        Status = status,
                        Token = token,
                        Message = "Registration complete. Welcome to Lontsi Homes."
                    });
                }

                return Ok(new
                {
                    Email = user.Email,
                    NextStep = status.NextStep,
                    Status = status,
                    Message = "Some verification steps are still pending."
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "VerifyLandlordMobilePayments", "Unable to verify mobile payment OTPs right now. Please try again.");
            }
        }

        [HttpPost("landlord-registration/kyc")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(40 * 1024 * 1024)]
        public async Task<IActionResult> SubmitLandlordKyc([FromForm] SubmitLandlordKycRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Email))
                {
                    return BadRequest("Email is required.");
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                var currentStatus = await BuildLandlordOnboardingStatusAsync(user);
                if (currentStatus.NextStep is LandlordOnboardingSteps.Email
                    or LandlordOnboardingSteps.Phone
                    or LandlordOnboardingSteps.MobilePayments
                    or LandlordOnboardingSteps.MobilePaymentVerification)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "LANDLORD_ONBOARDING_INCOMPLETE",
                        Email = user.Email,
                        NextStep = currentStatus.NextStep,
                        Status = currentStatus,
                        Message = "Complete the previous registration steps before identity verification."
                    });
                }

                if (request.DocumentType is not KycDocumentTypeEnum.NationalId
                    and not KycDocumentTypeEnum.Passport
                    and not KycDocumentTypeEnum.DriverLicense
                    and not KycDocumentTypeEnum.ResidencePermit
                    and not KycDocumentTypeEnum.Other)
                {
                    return BadRequest("Choose a valid identity document type.");
                }

                var profile = await _context.LandlordKycProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
                var isPartialResubmission =
                    profile?.Status == LandlordKycStatusEnum.Rejected &&
                    profile.DocumentType == request.DocumentType &&
                    HasRejectedKycFileFlags(profile);

                var documentBackRequired = RequiresDocumentBack(request.DocumentType);
                var requireFaceFront = !isPartialResubmission || profile!.RejectFaceFront;
                var requireFaceRight = !isPartialResubmission || profile!.RejectFaceRight;
                var requireFaceLeft = !isPartialResubmission || profile!.RejectFaceLeft;
                var requireDocumentFront = !isPartialResubmission || profile!.RejectDocumentFront;
                var requireDocumentBack = documentBackRequired &&
                    (!isPartialResubmission || profile!.RejectDocumentBack || string.IsNullOrWhiteSpace(profile.DocumentBackPath));

                var missingFiles = new List<string>();
                if (requireFaceFront && request.FaceFront == null) missingFiles.Add("front-facing photo");
                if (requireFaceRight && request.FaceRight == null) missingFiles.Add("photo looking right");
                if (requireFaceLeft && request.FaceLeft == null) missingFiles.Add("photo looking left");
                if (requireDocumentFront && request.DocumentFront == null) missingFiles.Add("front of the ID document");
                if (requireDocumentBack && request.DocumentBack == null) missingFiles.Add("back of the ID document");

                if (missingFiles.Any())
                {
                    return BadRequest($"Upload the required KYC file(s): {string.Join(", ", missingFiles)}.");
                }

                if ((request.FaceFront != null && !IsSupportedImage(request.FaceFront))
                    || (request.FaceRight != null && !IsSupportedImage(request.FaceRight))
                    || (request.FaceLeft != null && !IsSupportedImage(request.FaceLeft))
                    || (request.DocumentFront != null && !IsSupportedImage(request.DocumentFront)))
                {
                    return BadRequest("Face photos and the front ID document must be JPG, PNG, or WEBP images.");
                }

                if (request.DocumentBack != null && (!documentBackRequired || (!IsSupportedImage(request.DocumentBack) && !IsSupportedPdf(request.DocumentBack))))
                {
                    return BadRequest(documentBackRequired
                        ? "The back document must be an image or PDF file."
                        : "The selected document type does not require a back document.");
                }

                var now = DateTimeOffset.UtcNow;
                var previousFilesToDelete = new List<string>();

                if (profile == null)
                {
                    profile = new LandlordKycProfile
                    {
                        UserId = user.Id,
                        CreatedAt = now
                    };
                    _context.LandlordKycProfiles.Add(profile);
                }

                profile.DocumentType = request.DocumentType;
                profile.Status = LandlordKycStatusEnum.Submitted;

                if (request.FaceFront != null)
                {
                    previousFilesToDelete.Add(profile.FaceFrontPath);
                    var stored = await _kycFileStorageService.SaveAsync(user.Id, "face-front", request.FaceFront);
                    profile.FaceFrontPath = stored.RelativePath;
                    profile.FaceFrontContentType = stored.ContentType;
                    profile.FaceFrontOriginalFileName = stored.OriginalFileName;
                }

                if (request.FaceRight != null)
                {
                    previousFilesToDelete.Add(profile.FaceRightPath);
                    var stored = await _kycFileStorageService.SaveAsync(user.Id, "face-right", request.FaceRight);
                    profile.FaceRightPath = stored.RelativePath;
                    profile.FaceRightContentType = stored.ContentType;
                    profile.FaceRightOriginalFileName = stored.OriginalFileName;
                }

                if (request.FaceLeft != null)
                {
                    previousFilesToDelete.Add(profile.FaceLeftPath);
                    var stored = await _kycFileStorageService.SaveAsync(user.Id, "face-left", request.FaceLeft);
                    profile.FaceLeftPath = stored.RelativePath;
                    profile.FaceLeftContentType = stored.ContentType;
                    profile.FaceLeftOriginalFileName = stored.OriginalFileName;
                }

                if (request.DocumentFront != null)
                {
                    previousFilesToDelete.Add(profile.DocumentFrontPath);
                    var stored = await _kycFileStorageService.SaveAsync(user.Id, "document-front", request.DocumentFront);
                    profile.DocumentFrontPath = stored.RelativePath;
                    profile.DocumentFrontContentType = stored.ContentType;
                    profile.DocumentFrontOriginalFileName = stored.OriginalFileName;
                }

                if (request.DocumentBack != null)
                {
                    previousFilesToDelete.Add(profile.DocumentBackPath ?? string.Empty);
                    var stored = await _kycFileStorageService.SaveAsync(user.Id, "document-back", request.DocumentBack);
                    profile.DocumentBackPath = stored.RelativePath;
                    profile.DocumentBackContentType = stored.ContentType;
                    profile.DocumentBackOriginalFileName = stored.OriginalFileName;
                }
                else if (!documentBackRequired)
                {
                    previousFilesToDelete.Add(profile.DocumentBackPath ?? string.Empty);
                    profile.DocumentBackPath = null;
                    profile.DocumentBackContentType = null;
                    profile.DocumentBackOriginalFileName = null;
                }

                profile.SubmittedAt = now;
                profile.ReviewedAt = null;
                profile.ReviewedById = null;
                profile.ReviewNote = null;
                ClearKycRejectionFlags(profile);
                profile.UpdatedAt = now;

                await _context.SaveChangesAsync();
                await _kycFileStorageService.DeleteFilesAsync(previousFilesToDelete);

                var status = await BuildLandlordOnboardingStatusAsync(user);
                return Ok(new
                {
                    Email = user.Email,
                    NextStep = status.NextStep,
                    Status = status,
                    Kyc = BuildKycSummary(profile),
                    Message = "Identity verification submitted. Review the platform contract to finish registration."
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return ServerError(ex, "SubmitLandlordKyc", "Unable to submit identity verification right now. Please try again.");
            }
        }

        [HttpPost("landlord-registration/contract")]
        public async Task<IActionResult> SignLandlordContract([FromBody] SubmitLandlordContractRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                if (!request.Accepted)
                {
                    return BadRequest("Accept the platform terms before signing.");
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                var currentStatus = await BuildLandlordOnboardingStatusAsync(user);
                if (currentStatus.NextStep != LandlordOnboardingSteps.Contract &&
                    currentStatus.NextStep != LandlordOnboardingSteps.Complete)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "LANDLORD_ONBOARDING_INCOMPLETE",
                        Email = user.Email,
                        NextStep = currentStatus.NextStep,
                        Status = currentStatus,
                        Message = "Complete identity verification before signing the platform contract."
                    });
                }

                user.PlatformTermsAccepted = true;
                user.PlatformTermsAcceptedAt = DateTimeOffset.UtcNow;
                user.PlatformTermsSignatureName = request.SignatureName.Trim();
                user.PlatformTermsVersion = PlatformTermsVersion;

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                var status = await BuildLandlordOnboardingStatusAsync(user);
                if (status.IsComplete)
                {
                    var token = await _tokenService.GenerateTokenAsync(user);
                    return Ok(new
                    {
                        Email = user.Email,
                        NextStep = status.NextStep,
                        Status = status,
                        Token = token,
                        Message = "Registration complete. Welcome to Lontsi Homes."
                    });
                }

                return Ok(new
                {
                    Email = user.Email,
                    NextStep = status.NextStep,
                    Status = status,
                    Message = "Contract signed. Continue the remaining registration steps."
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "SignLandlordContract", "Unable to save your platform contract signature right now. Please try again.");
            }
        }

        [HttpPost("register-visitor")]
        public async Task<IActionResult> RegisterVisitor([FromBody] RegisterVisitorRequest request)
        {
            ApplicationUser? createdUser = null;

            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var existingUser = await _userManager.FindByEmailAsync(request.Email);
                if (existingUser != null)
                {
                    return BadRequest("An account with this email already exists. Please log in instead.");
                }

                var user = new ApplicationUser
                {
                    UserName = request.Email,
                    Email = request.Email,
                    FullName = request.FullName?.Trim(),
                    PhoneNumber = request.PhoneNumber.Trim(),
                    WhatsAppPhoneNumber = request.WhatsAppPhoneNumber.Trim(),
                    EmailConfirmed = false,
                    PhoneNumberConfirmed = false,
                    IsWhatsAppPhoneVerified = false
                };

                var result = await _userManager.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                createdUser = user;

                var roleResult = await _userManager.AddToRoleAsync(user, "Visitor");
                if (!roleResult.Succeeded)
                {
                    await _userManager.DeleteAsync(user);
                    createdUser = null;
                    return BadRequest(roleResult.Errors);
                }

                await _userOnboardingService.SendVisitorActivationOtpAsync(user);

                return Ok(new
                {
                    RequiresActivation = true,
                    Email = user.Email,
                    Message = "Visitor account created. Check your email, phone number, and WhatsApp for OTP codes to activate your account."
                });
            }
            catch (OtpSendThrottledException ex)
            {
                await TryRollbackRegistrationAsync(createdUser);
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                await TryRollbackRegistrationAsync(createdUser);

                _logger.LogError(ex, "Visitor registration failed for email {Email}", request.Email);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "VISITOR_REGISTRATION_FAILED",
                    Message = "We could not complete visitor account creation right now. Please try again in a few minutes."
                });
            }
        }

        /// <summary>
        /// Verifies account activation OTP and activates the account.
        /// </summary>
        [HttpPost("verify-activation-otp")]
        public async Task<IActionResult> VerifyActivationOtp([FromBody] VerifyActivationOtpRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid email or OTP.");
                }

                if (user.EmailConfirmed)
                {
                    return BadRequest("Account is already activated.");
                }

                var emailOtp = string.IsNullOrWhiteSpace(request.EmailOtp)
                    ? request.Otp?.Trim() ?? string.Empty
                    : request.EmailOtp.Trim();

                var emailOtpResult = await ValidateOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName, emailOtp, "email");
                if (emailOtpResult != null)
                {
                    return emailOtpResult;
                }

                if (!string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
                {
                    var payoutOtpResult = await ValidateOtpAsync(user, PayoutOtpTokenName, PayoutOtpExpiryTokenName, request.PayoutOtp.Trim(), "payout number");
                    if (payoutOtpResult != null)
                    {
                        return payoutOtpResult;
                    }
                }

                if (!string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
                {
                    var whatsAppOtpResult = await ValidateOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName, request.WhatsAppOtp.Trim(), "WhatsApp");
                    if (whatsAppOtpResult != null)
                    {
                        return whatsAppOtpResult;
                    }
                }

                user.EmailConfirmed = true;
                if (!string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
                {
                    user.IsPayoutPhoneVerified = true;
                    user.PayoutPhoneVerifiedAt = DateTimeOffset.UtcNow;
                }

                if (!string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
                {
                    if (SamePhone(user.SubscriptionPaymentPhoneNumber, user.PayoutPhoneNumber) && user.IsPayoutPhoneVerified)
                    {
                        user.IsSubscriptionPaymentPhoneVerified = true;
                        user.SubscriptionPaymentPhoneVerifiedAt = DateTimeOffset.UtcNow;
                    }
                }

                if (!string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
                {
                    user.IsWhatsAppPhoneVerified = true;
                    user.WhatsAppPhoneVerifiedAt = DateTimeOffset.UtcNow;
                }
                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                await RemoveOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName);
                await RemoveOtpAsync(user, PayoutOtpTokenName, PayoutOtpExpiryTokenName);
                await RemoveOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName);

                var token = await _tokenService.GenerateTokenAsync(user);
                return Ok(new { Message = "Account activated successfully.", Token = token });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "VerifyActivationOtp", "Unable to verify OTP right now. Please try again.");
            }
        }

        [HttpPost("verify-visitor-otp")]
        public async Task<IActionResult> VerifyVisitorOtp([FromBody] VerifyVisitorOtpRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid email or OTP.");
                }

                if (!await _userManager.IsInRoleAsync(user, "Visitor"))
                {
                    return BadRequest("This account is not registered as a visitor account.");
                }

                var emailOtpResult = await ValidateOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName, request.EmailOtp.Trim(), "email");
                if (emailOtpResult != null)
                {
                    return emailOtpResult;
                }

                var phoneOtpResult = await ValidateOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName, request.PhoneOtp.Trim(), "phone number");
                if (phoneOtpResult != null)
                {
                    return phoneOtpResult;
                }

                var whatsAppOtpResult = await ValidateOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName, request.WhatsAppOtp.Trim(), "WhatsApp");
                if (whatsAppOtpResult != null)
                {
                    return whatsAppOtpResult;
                }

                user.EmailConfirmed = true;
                user.PhoneNumberConfirmed = true;
                user.IsWhatsAppPhoneVerified = true;
                user.WhatsAppPhoneVerifiedAt = DateTimeOffset.UtcNow;

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                await RemoveOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName);
                await RemoveOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName);
                await RemoveOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName);

                var token = await _tokenService.GenerateTokenAsync(user);
                return Ok(new { Message = "Visitor account activated successfully.", Token = token });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "VerifyVisitorOtp", "Unable to verify visitor OTP right now. Please try again.");
            }
        }

        /// <summary>
        /// Resends activation OTP for non-activated accounts.
        /// </summary>
        [HttpPost("resend-activation-otp")]
        public async Task<IActionResult> ResendActivationOtp([FromBody] ResendActivationOtpRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null)
                {
                    return Ok(new { Message = "If the account exists, a new OTP has been sent." });
                }

                if (user.EmailConfirmed)
                {
                    var payoutReady = string.IsNullOrWhiteSpace(user.PayoutPhoneNumber) || user.IsPayoutPhoneVerified;
                    var whatsAppReady = string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) || user.IsWhatsAppPhoneVerified;
                    if (payoutReady && whatsAppReady)
                    {
                        return BadRequest("Account is already activated.");
                    }
                }

                await _userOnboardingService.SendActivationOtpAsync(user);
                return Ok(new { Message = "New OTP codes have been sent to your email, payout number, and WhatsApp." });
            }
            catch (OtpSendThrottledException ex)
            {
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                return ServerError(ex, "ResendActivationOtp", "Unable to resend OTP right now. Please try again.");
            }
        }

        [HttpPost("resend-visitor-otp")]
        public async Task<IActionResult> ResendVisitorOtp([FromBody] ResendActivationOtpRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null)
                {
                    return Ok(new { Message = "If the visitor account exists, new OTP codes have been sent." });
                }

                if (!await _userManager.IsInRoleAsync(user, "Visitor"))
                {
                    return BadRequest("This account is not registered as a visitor account.");
                }

                if (user.EmailConfirmed && user.PhoneNumberConfirmed && user.IsWhatsAppPhoneVerified)
                {
                    return BadRequest("Visitor account is already activated.");
                }

                await _userOnboardingService.SendVisitorActivationOtpAsync(user);
                return Ok(new { Message = "New OTP codes have been sent to your email, phone number, and WhatsApp." });
            }
            catch (OtpSendThrottledException ex)
            {
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                return ServerError(ex, "ResendVisitorOtp", "Unable to resend visitor OTP right now. Please try again.");
            }
        }

        /// <summary>
        /// Authenticates a user and returns a JWT token on success.
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null)
                {
                    return Unauthorized("Invalid email or password.");
                }

                var valid = await _userManager.CheckPasswordAsync(user, request.Password);
                if (!valid)
                {
                    return Unauthorized("Invalid email or password.");
                }

                var roles = (await _userManager.GetRolesAsync(user)).ToList();
                var isAdmin = roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
                var isVisitor = roles.Any(r => string.Equals(r, "Visitor", StringComparison.OrdinalIgnoreCase));
                var isLandlord = roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase));

                if (isAdmin)
                {
                    var adminToken = await _tokenService.GenerateTokenAsync(user);
                    return Ok(new { Token = adminToken });
                }

                if (isLandlord)
                {
                    var status = await BuildLandlordOnboardingStatusAsync(user, roles);
                    if (!status.IsComplete)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new
                        {
                            Code = "LANDLORD_ONBOARDING_INCOMPLETE",
                            Email = user.Email,
                            NextStep = status.NextStep,
                            Status = status,
                            Message = "Your landlord registration is not complete yet. Continue from the saved step."
                        });
                    }
                }

                if (!user.EmailConfirmed)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = isVisitor ? "VISITOR_ACCOUNT_VERIFICATION_PENDING" : "EMAIL_NOT_CONFIRMED",
                        Email = user.Email,
                        Message = isVisitor
                            ? "Visitor account is not activated. Verify the OTP codes sent to your email, phone number, and WhatsApp."
                            : "Account is not activated. Verify all OTP codes to complete account activation."
                    });
                }

                if (isVisitor)
                {
                    var visitorPhonePending = !string.IsNullOrWhiteSpace(user.PhoneNumber) && !user.PhoneNumberConfirmed;
                    var visitorWhatsAppPending = !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) && !user.IsWhatsAppPhoneVerified;
                    if (visitorPhonePending || visitorWhatsAppPending)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new
                        {
                            Code = "VISITOR_ACCOUNT_VERIFICATION_PENDING",
                            Email = user.Email,
                            Message = "Your visitor verification is still pending. Verify the OTP codes sent to your email, phone number, and WhatsApp."
                        });
                    }
                }
                else if (!isLandlord)
                {
                    var payoutVerificationPending = !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber) && !user.IsPayoutPhoneVerified;
                    var whatsAppVerificationPending = !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) && !user.IsWhatsAppPhoneVerified;
                    if (payoutVerificationPending || whatsAppVerificationPending)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new
                        {
                            Code = "ACCOUNT_VERIFICATION_PENDING",
                            Email = user.Email,
                            Message = "Your contact verification is still pending. Verify all OTP codes to complete account activation."
                        });
                    }
                }

                var token = await _tokenService.GenerateTokenAsync(user);
                return Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "Login", "Unable to sign in right now. Please try again.");
            }
        }

        [HttpPost("refresh")]
        [Authorize]
        public async Task<IActionResult> Refresh()
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    return Unauthorized();
                }

                var token = await _tokenService.GenerateTokenAsync(user);
                return Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "Refresh", "Unable to extend your session right now. Please sign in again.");
            }
        }

        [HttpGet("profile-overview")]
        [Authorize]
        public async Task<IActionResult> GetProfileOverview()
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var user = await _userManager.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null)
                {
                    return Unauthorized();
                }

                var roles = (await _userManager.GetRolesAsync(user)).ToList();
                var isAdmin = roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
                var now = DateTimeOffset.UtcNow;

                var ownedPropertyIds = await _context.Properties
                    .Where(p => p.LandlordId == userId)
                    .Select(p => p.Id)
                    .ToListAsync();

                var managedPropertyIds = await _context.PropertyManagerAssignments
                    .Where(m => m.ManagerId == userId)
                    .Select(m => m.PropertyId)
                    .ToListAsync();

                var ownerPropertyIds = await _context.ApartmentOwners
                    .Where(o => o.OwnerId == userId)
                    .Join(_context.Apartments, o => o.ApartmentId, a => a.Id, (o, a) => a.PropertyId)
                    .Distinct()
                    .ToListAsync();

                var tenantPropertyIds = await _context.Tenancies
                    .Where(t =>
                        t.Members.Any(m => !m.IsDeleted && m.MemberId == userId) &&
                        (t.EndDate == null || t.EndDate > now))
                    .Join(_context.Apartments, t => t.ApartmentId, a => a.Id, (t, a) => a.PropertyId)
                    .Distinct()
                    .ToListAsync();

                List<int> accessiblePropertyIds;
                if (isAdmin)
                {
                    accessiblePropertyIds = await _context.Properties
                        .Select(p => p.Id)
                        .ToListAsync();
                }
                else
                {
                    accessiblePropertyIds = ownedPropertyIds
                        .Union(managedPropertyIds)
                        .Union(ownerPropertyIds)
                        .Union(tenantPropertyIds)
                        .Distinct()
                        .ToList();
                }

                var ownedSet = ownedPropertyIds.ToHashSet();
                var managedSet = managedPropertyIds.ToHashSet();
                var ownerSet = ownerPropertyIds.ToHashSet();
                var tenantSet = tenantPropertyIds.ToHashSet();

                string ResolveAccessSource(int propertyId)
                {
                    if (isAdmin) return "Admin";
                    if (ownedSet.Contains(propertyId)) return "Owned";
                    if (managedSet.Contains(propertyId)) return "Managed";
                    if (ownerSet.Contains(propertyId)) return "Owner";
                    if (tenantSet.Contains(propertyId)) return "Tenant";
                    return "Shared";
                }

                var properties = await _context.Properties
                    .Where(p => accessiblePropertyIds.Contains(p.Id))
                    .OrderBy(p => p.Name)
                    .Select(p => new ProfilePropertyDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        City = p.City,
                        Address = p.Address,
                        ApartmentCount = p.Apartments.Count(),
                        AccessSource = string.Empty
                    })
                    .ToListAsync();

                foreach (var property in properties)
                {
                    property.AccessSource = ResolveAccessSource(property.Id);
                }

                var activeSubscription = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .Where(us => us.UserId == userId && !us.IsDeleted && us.IsApproved && us.EndDate > now)
                    .OrderByDescending(us => us.EndDate)
                    .ThenByDescending(us => us.StartDate)
                    .FirstOrDefaultAsync();

                var pendingSubscription = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .Where(us =>
                        us.UserId == userId &&
                        !us.IsDeleted &&
                        !us.IsApproved &&
                        us.PaymentStatus != PaymentStatusEnum.Success &&
                        us.EndDate > now)
                    .OrderByDescending(us => us.UpdatedAt ?? us.CreatedAt)
                    .FirstOrDefaultAsync();

                var landlordStatus = roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase))
                    ? await BuildLandlordOnboardingStatusAsync(user, roles)
                    : null;

                var plans = await _context.SubscriptionPlans
                    .OrderBy(p => p.Price)
                    .Select(p => new SubscriptionPlanDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Price = p.Price,
                        DurationInDays = p.DurationInDays,
                        Description = p.Description ?? string.Empty,
                        MaxProperties = p.MaxProperties,
                        MaxApartmentsPerProperty = p.MaxApartmentsPerProperty
                    })
                    .ToListAsync();

                var fullName = (user.FullName ?? string.Empty).Trim();
                var firstName = fullName;
                var lastName = string.Empty;
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    var parts = fullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    firstName = parts.Length > 0 ? parts[0] : string.Empty;
                    lastName = parts.Length > 1 ? parts[1] : string.Empty;
                }

                var dto = new ProfileOverviewDto
                {
                    UserId = user.Id,
                    Email = user.Email ?? string.Empty,
                    FirstName = firstName,
                    LastName = lastName,
                    FullName = fullName,
                    CountryCode = user.CountryCode,
                    PhoneNumber = user.PhoneNumber,
                    SubscriptionPaymentPhoneNumber = user.SubscriptionPaymentPhoneNumber,
                    SubscriptionPaymentChannel = user.SubscriptionPaymentChannel,
                    IsSubscriptionPaymentPhoneVerified = user.IsSubscriptionPaymentPhoneVerified,
                    PayoutPhoneNumber = user.PayoutPhoneNumber,
                    PayoutChannel = user.PayoutChannel,
                    IsPayoutPhoneVerified = user.IsPayoutPhoneVerified,
                    WhatsAppPhoneNumber = user.WhatsAppPhoneNumber,
                    IsWhatsAppPhoneVerified = user.IsWhatsAppPhoneVerified,
                    Roles = roles.ToList(),
                    KycDocumentType = landlordStatus?.KycDocumentType,
                    KycStatus = landlordStatus?.KycStatus ?? LandlordKycStatusEnum.NotStarted,
                    IsKycSubmitted = landlordStatus?.IsKycSubmitted ?? false,
                    IsKycApproved = landlordStatus?.IsKycApproved ?? false,
                    KycSubmittedAt = landlordStatus?.KycSubmittedAt,
                    KycReviewedAt = landlordStatus?.KycReviewedAt,
                    KycReviewNote = landlordStatus?.KycReviewNote,
                    KycRejectedFiles = landlordStatus?.KycRejectedFiles ?? new LandlordKycRejectedFilesDto(),
                    PlatformTermsAccepted = user.PlatformTermsAccepted,
                    PlatformTermsAcceptedAt = user.PlatformTermsAcceptedAt,
                    PlatformTermsSignatureName = user.PlatformTermsSignatureName,
                    PlatformTermsVersion = user.PlatformTermsVersion,
                    NextOnboardingStep = landlordStatus?.NextStep ?? LandlordOnboardingSteps.Complete,
                    CanStartSubscriptionCheckout = CanStartSubscriptionCheckout(roles, landlordStatus, user),
                    SubscriptionBlockedReason = ResolveSubscriptionBlockedReason(roles, landlordStatus, user),
                    PropertyCount = properties.Count,
                    ApartmentCount = properties.Sum(p => p.ApartmentCount),
                    HasActiveSubscription = activeSubscription != null,
                    SubscriptionApproved = activeSubscription?.IsApproved ?? false,
                    CurrentPlanId = activeSubscription?.SubscriptionPlanId,
                    CurrentPlanName = !string.IsNullOrWhiteSpace(activeSubscription?.PlanNameSnapshot)
                        ? activeSubscription.PlanNameSnapshot
                        : activeSubscription?.SubscriptionPlan?.Name ?? string.Empty,
                    CurrentPlanPrice = activeSubscription?.PlanPriceSnapshot,
                    CurrentPlanDurationInDays = activeSubscription?.PlanDurationInDaysSnapshot,
                    SubscriptionStartDate = activeSubscription?.StartDate,
                    SubscriptionEndDate = activeSubscription?.EndDate,
                    PendingSubscriptionId = pendingSubscription?.Id,
                    PendingPlanId = pendingSubscription?.SubscriptionPlanId,
                    PendingPlanName = !string.IsNullOrWhiteSpace(pendingSubscription?.PlanNameSnapshot)
                        ? pendingSubscription.PlanNameSnapshot
                        : pendingSubscription?.SubscriptionPlan?.Name ?? string.Empty,
                    PendingPlanPrice = pendingSubscription?.PlanPriceSnapshot,
                    PendingPaymentStatus = pendingSubscription?.PaymentStatus,
                    PendingPaymentMethod = pendingSubscription?.PaymentMethod,
                    PendingPaymentReference = pendingSubscription?.PaymentReference ?? string.Empty,
                    Properties = properties,
                    AvailablePlans = plans
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return ServerError(ex, "GetProfileOverview", "Unable to load profile information right now. Please try again.");
            }
        }

        /// <summary>
        /// Changes the password of the current user.
        /// </summary>
        [HttpPost("change-password")]
        [Authorize]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized();
                }

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    return Unauthorized();
                }

                var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                return Ok(new { Message = "Password changed successfully." });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "ChangePassword", "Unable to change password right now. Please try again.");
            }
        }

        /// <summary>
        /// Generates a password reset token and returns it. In production this token should be emailed.
        /// </summary>
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            try
            {
                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null)
                {
                    return Ok(new { Message = "If the email exists, a reset token has been generated." });
                }

                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                return Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "ForgotPassword", "Unable to process password reset right now. Please try again.");
            }
        }

        /// <summary>
        /// Resets the password using a token obtained from ForgotPassword.
        /// </summary>
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            try
            {
                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid email.");
                }

                var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                return Ok(new { Message = "Password has been reset successfully." });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "ResetPassword", "Unable to reset password right now. Please try again.");
            }
        }

        /// <summary>
        /// Logs the user out. With JWT this is typically handled on the client by discarding the token.
        /// </summary>
        [HttpPost("logout")]
        [Authorize]
        public IActionResult Logout()
        {
            try
            {
                return Ok(new { Message = "Logged out." });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "Logout", "Unable to complete logout right now. Please try again.");
            }
        }

        private async Task<ApplicationUser?> FindLandlordForOnboardingAsync(string email)
        {
            var user = await _userManager.FindByEmailAsync((email ?? string.Empty).Trim());
            if (user == null)
            {
                return null;
            }

            return await _userManager.IsInRoleAsync(user, "Landlord") ? user : null;
        }

        private async Task<LandlordOnboardingStatusDto> BuildLandlordOnboardingStatusAsync(
            ApplicationUser user,
            IEnumerable<string>? knownRoles = null)
        {
            var roles = knownRoles?.ToList() ?? (await _userManager.GetRolesAsync(user)).ToList();
            var (firstName, lastName) = SplitFullName(user.FullName);
            var kycProfile = roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase))
                ? await _context.LandlordKycProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == user.Id)
                : null;

            var status = new LandlordOnboardingStatusDto
            {
                UserId = user.Id,
                Email = user.Email ?? string.Empty,
                FirstName = firstName,
                LastName = lastName,
                FullName = user.FullName ?? string.Empty,
                CountryCode = user.CountryCode,
                PhoneNumber = user.PhoneNumber,
                EmailConfirmed = user.EmailConfirmed,
                PhoneNumberConfirmed = user.PhoneNumberConfirmed,
                UsePrimaryPhoneForSubscriptionPayments = user.UsePrimaryPhoneForSubscriptionPayments,
                SubscriptionPaymentPhoneNumber = user.SubscriptionPaymentPhoneNumber,
                SubscriptionPaymentChannel = user.SubscriptionPaymentChannel,
                IsSubscriptionPaymentPhoneVerified = user.IsSubscriptionPaymentPhoneVerified,
                UsePrimaryPhoneForRentPayouts = user.UsePrimaryPhoneForRentPayouts,
                PayoutPhoneNumber = user.PayoutPhoneNumber,
                PayoutChannel = user.PayoutChannel,
                IsPayoutPhoneVerified = user.IsPayoutPhoneVerified,
                WhatsAppPhoneNumber = user.WhatsAppPhoneNumber,
                IsWhatsAppPhoneVerified = user.IsWhatsAppPhoneVerified,
                KycDocumentType = kycProfile?.DocumentType,
                KycStatus = kycProfile?.Status ?? LandlordKycStatusEnum.NotStarted,
                IsKycSubmitted = kycProfile?.Status is LandlordKycStatusEnum.Submitted or LandlordKycStatusEnum.Approved,
                IsKycApproved = kycProfile?.Status == LandlordKycStatusEnum.Approved,
                KycSubmittedAt = kycProfile?.SubmittedAt,
                KycReviewedAt = kycProfile?.ReviewedAt,
                KycReviewNote = kycProfile?.ReviewNote,
                KycRejectedFiles = BuildRejectedFiles(kycProfile),
                PlatformTermsAccepted = user.PlatformTermsAccepted,
                PlatformTermsAcceptedAt = user.PlatformTermsAcceptedAt,
                PlatformTermsSignatureName = user.PlatformTermsSignatureName,
                PlatformTermsVersion = user.PlatformTermsVersion,
                CreatedAt = user.CreatedAt,
                Roles = roles
            };

            status.PhoneOtpRequestLimit = await BuildOtpRequestLimitAsync(user, OtpSendPurposes.LandlordPhone);
            status.SubscriptionPaymentOtpRequestLimit = await BuildOtpRequestLimitAsync(user, OtpSendPurposes.SubscriptionPaymentPhone);
            status.PayoutOtpRequestLimit = await BuildOtpRequestLimitAsync(user, OtpSendPurposes.RentPayoutPhone);
            status.WhatsAppOtpRequestLimit = await BuildOtpRequestLimitAsync(user, OtpSendPurposes.WhatsAppPhone);

            status.NextStep = ResolveLandlordOnboardingStep(status);
            status.IsComplete = status.NextStep == LandlordOnboardingSteps.Complete;
            return status;
        }

        private async Task<OtpRequestLimitDto> BuildOtpRequestLimitAsync(ApplicationUser user, string purpose)
        {
            var status = await _userOnboardingService.GetTwilioOtpThrottleStatusAsync(user, purpose);
            return new OtpRequestLimitDto
            {
                DailyRequestLimit = status.DailyRequestLimit,
                DailyRequestsRemaining = status.DailyRequestsRemaining,
                RetryAfterSeconds = status.RetryAfterSeconds,
                DailyLimitReached = status.DailyLimitReached,
                NextAllowedAt = status.NextAllowedAt,
                DailyLimitResetsAt = status.DailyLimitResetsAt
            };
        }

        private static string ResolveLandlordOnboardingStep(LandlordOnboardingStatusDto status)
        {
            if (status.Roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase)))
            {
                return LandlordOnboardingSteps.Complete;
            }

            if (string.IsNullOrWhiteSpace(status.Email))
            {
                return LandlordOnboardingSteps.Account;
            }

            if (!status.EmailConfirmed)
            {
                return LandlordOnboardingSteps.Email;
            }

            if (string.IsNullOrWhiteSpace(status.PhoneNumber) || !status.PhoneNumberConfirmed)
            {
                return LandlordOnboardingSteps.Phone;
            }

            var hasSubscriptionPaymentDetails =
                !string.IsNullOrWhiteSpace(status.SubscriptionPaymentPhoneNumber) &&
                status.SubscriptionPaymentChannel is PayoutChannelEnum.MtnMoney or PayoutChannelEnum.OrangeMoney;

            var hasPayoutDetails =
                !string.IsNullOrWhiteSpace(status.PayoutPhoneNumber) &&
                status.PayoutChannel is PayoutChannelEnum.MtnMoney or PayoutChannelEnum.OrangeMoney;

            if (!hasSubscriptionPaymentDetails || !hasPayoutDetails)
            {
                return LandlordOnboardingSteps.MobilePayments;
            }

            var whatsAppReady = string.IsNullOrWhiteSpace(status.WhatsAppPhoneNumber) || status.IsWhatsAppPhoneVerified;
            if (!status.IsSubscriptionPaymentPhoneVerified || !status.IsPayoutPhoneVerified || !whatsAppReady)
            {
                return LandlordOnboardingSteps.MobilePaymentVerification;
            }

            if (!status.IsKycSubmitted)
            {
                return LandlordOnboardingSteps.Kyc;
            }

            if (!status.PlatformTermsAccepted)
            {
                return LandlordOnboardingSteps.Contract;
            }

            return LandlordOnboardingSteps.Complete;
        }

        private static LandlordOnboardingStatusDto SanitizeOnboardingStatusForAnonymous(LandlordOnboardingStatusDto status)
        {
            return new LandlordOnboardingStatusDto
            {
                Email = status.Email,
                EmailConfirmed = status.EmailConfirmed,
                PhoneNumberConfirmed = status.PhoneNumberConfirmed,
                UsePrimaryPhoneForSubscriptionPayments = status.UsePrimaryPhoneForSubscriptionPayments,
                SubscriptionPaymentChannel = status.SubscriptionPaymentChannel,
                IsSubscriptionPaymentPhoneVerified = status.IsSubscriptionPaymentPhoneVerified,
                UsePrimaryPhoneForRentPayouts = status.UsePrimaryPhoneForRentPayouts,
                PayoutChannel = status.PayoutChannel,
                IsPayoutPhoneVerified = status.IsPayoutPhoneVerified,
                IsWhatsAppPhoneVerified = status.IsWhatsAppPhoneVerified,
                KycDocumentType = status.KycDocumentType,
                KycStatus = status.KycStatus,
                IsKycSubmitted = status.IsKycSubmitted,
                IsKycApproved = status.IsKycApproved,
                KycSubmittedAt = status.KycSubmittedAt,
                KycReviewedAt = status.KycReviewedAt,
                KycReviewNote = status.KycReviewNote,
                KycRejectedFiles = status.KycRejectedFiles,
                PlatformTermsAccepted = status.PlatformTermsAccepted,
                PlatformTermsAcceptedAt = status.PlatformTermsAcceptedAt,
                PlatformTermsSignatureName = status.PlatformTermsSignatureName,
                PlatformTermsVersion = status.PlatformTermsVersion,
                PhoneOtpRequestLimit = status.PhoneOtpRequestLimit,
                SubscriptionPaymentOtpRequestLimit = status.SubscriptionPaymentOtpRequestLimit,
                PayoutOtpRequestLimit = status.PayoutOtpRequestLimit,
                WhatsAppOtpRequestLimit = status.WhatsAppOtpRequestLimit,
                NextStep = status.NextStep,
                IsComplete = status.IsComplete,
                CreatedAt = status.CreatedAt
            };
        }

        private static bool CanStartSubscriptionCheckout(
            IReadOnlyCollection<string> roles,
            LandlordOnboardingStatusDto? landlordStatus,
            ApplicationUser user)
        {
            if (!roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return landlordStatus?.IsKycApproved == true &&
                   user.PlatformTermsAccepted &&
                   user.IsSubscriptionPaymentPhoneVerified;
        }

        private static string ResolveSubscriptionBlockedReason(
            IReadOnlyCollection<string> roles,
            LandlordOnboardingStatusDto? landlordStatus,
            ApplicationUser user)
        {
            if (!roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase)))
            {
                return string.Empty;
            }

            if (landlordStatus == null)
            {
                return "Complete landlord registration before subscribing.";
            }

            if (!landlordStatus.IsKycSubmitted)
            {
                return "Submit identity verification before paying for a subscription.";
            }

            if (landlordStatus.KycStatus == LandlordKycStatusEnum.Rejected)
            {
                return string.IsNullOrWhiteSpace(landlordStatus.KycReviewNote)
                    ? "Identity verification was rejected. Submit corrected documents before subscribing."
                    : landlordStatus.KycReviewNote;
            }

            if (!landlordStatus.IsKycApproved)
            {
                return "Identity verification is waiting for admin approval before payments are unlocked.";
            }

            if (!user.PlatformTermsAccepted)
            {
                return "Sign the platform contract before paying for a subscription.";
            }

            if (!user.IsSubscriptionPaymentPhoneVerified)
            {
                return "Verify your subscription payment number before subscribing.";
            }

            return string.Empty;
        }

        private static LandlordKycSummaryDto BuildKycSummary(LandlordKycProfile? profile, string? mediaBaseUrl = null)
        {
            if (profile == null)
            {
                return new LandlordKycSummaryDto();
            }

            var summary = new LandlordKycSummaryDto
            {
                HasProfile = true,
                DocumentType = profile.DocumentType,
                Status = profile.Status,
                SubmittedAt = profile.SubmittedAt,
                ReviewedAt = profile.ReviewedAt,
                ReviewNote = profile.ReviewNote,
                RejectedFiles = BuildRejectedFiles(profile)
            };

            AddKycMedia(summary, "face-front", "Face - front", profile.FaceFrontOriginalFileName, profile.FaceFrontContentType, mediaBaseUrl);
            AddKycMedia(summary, "face-right", "Face - looking right", profile.FaceRightOriginalFileName, profile.FaceRightContentType, mediaBaseUrl);
            AddKycMedia(summary, "face-left", "Face - looking left", profile.FaceLeftOriginalFileName, profile.FaceLeftContentType, mediaBaseUrl);
            AddKycMedia(summary, "document-front", "Document front", profile.DocumentFrontOriginalFileName, profile.DocumentFrontContentType, mediaBaseUrl);
            if (!string.IsNullOrWhiteSpace(profile.DocumentBackPath))
            {
                AddKycMedia(summary, "document-back", "Document back", profile.DocumentBackOriginalFileName ?? string.Empty, profile.DocumentBackContentType ?? string.Empty, mediaBaseUrl);
            }

            return summary;
        }

        private static void AddKycMedia(
            LandlordKycSummaryDto summary,
            string key,
            string label,
            string originalFileName,
            string contentType,
            string? mediaBaseUrl)
        {
            summary.Media.Add(new LandlordKycMediaDto
            {
                Key = key,
                Label = label,
                OriginalFileName = originalFileName,
                ContentType = contentType,
                IsImage = IsImageContentType(contentType),
                Url = string.IsNullOrWhiteSpace(mediaBaseUrl) ? null : $"{mediaBaseUrl}/{key}"
            });
        }

        private static bool RequiresDocumentBack(KycDocumentTypeEnum documentType)
        {
            return documentType is KycDocumentTypeEnum.NationalId
                or KycDocumentTypeEnum.DriverLicense
                or KycDocumentTypeEnum.ResidencePermit
                or KycDocumentTypeEnum.Other;
        }

        private static LandlordKycRejectedFilesDto BuildRejectedFiles(LandlordKycProfile? profile)
        {
            if (profile == null)
            {
                return new LandlordKycRejectedFilesDto();
            }

            return new LandlordKycRejectedFilesDto
            {
                FaceFront = profile.RejectFaceFront,
                FaceRight = profile.RejectFaceRight,
                FaceLeft = profile.RejectFaceLeft,
                DocumentFront = profile.RejectDocumentFront,
                DocumentBack = profile.RejectDocumentBack && RequiresDocumentBack(profile.DocumentType)
            };
        }

        private static bool HasRejectedKycFileFlags(LandlordKycProfile profile)
        {
            return profile.RejectFaceFront
                || profile.RejectFaceRight
                || profile.RejectFaceLeft
                || profile.RejectDocumentFront
                || (profile.RejectDocumentBack && RequiresDocumentBack(profile.DocumentType));
        }

        private static void ClearKycRejectionFlags(LandlordKycProfile profile)
        {
            profile.RejectFaceFront = false;
            profile.RejectFaceRight = false;
            profile.RejectFaceLeft = false;
            profile.RejectDocumentFront = false;
            profile.RejectDocumentBack = false;
        }

        private static bool IsSupportedImage(IFormFile file)
        {
            if (file.Length <= 0)
            {
                return false;
            }

            var contentType = file.ContentType ?? string.Empty;
            var extension = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
            return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                   extension is ".jpg" or ".jpeg" or ".png" or ".webp";
        }

        private static bool IsSupportedPdf(IFormFile file)
        {
            var contentType = file.ContentType ?? string.Empty;
            var extension = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
            return string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
                   extension == ".pdf";
        }

        private static bool IsImageContentType(string? contentType)
        {
            return !string.IsNullOrWhiteSpace(contentType) &&
                   contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        private static (string FirstName, string LastName) SplitFullName(string? fullName)
        {
            var value = (fullName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return (string.Empty, string.Empty);
            }

            var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
        }

        private IActionResult? ValidateCameroonMobileMoneyNumber(
            string? phoneNumber,
            PayoutChannelEnum? channel,
            string label,
            out string normalizedPhoneNumber)
        {
            normalizedPhoneNumber = string.Empty;
            if (!CameroonMobileMoneyNumberHelper.TryNormalizeNationalNumber(phoneNumber, out normalizedPhoneNumber))
            {
                return BadRequest(new
                {
                    Code = "MOBILE_MONEY_PHONE_INVALID",
                    Message = $"The {label} must be a valid Cameroon Mobile Money number. {CameroonMobileMoneyNumberHelper.SupportedPrefixesDescription}"
                });
            }

            var detectedChannel = CameroonMobileMoneyNumberHelper.ResolveOperator(normalizedPhoneNumber);
            if (detectedChannel != channel)
            {
                return BadRequest(new
                {
                    Code = "MOBILE_MONEY_OPERATOR_MISMATCH",
                    Message = $"The {label} looks like {CameroonMobileMoneyNumberHelper.ChannelLabel(detectedChannel)}. Choose {CameroonMobileMoneyNumberHelper.ChannelLabel(detectedChannel)} or use a number that matches {CameroonMobileMoneyNumberHelper.ChannelLabel(channel)}."
                });
            }

            return null;
        }

        private static string NormalizeCountryCode(string? countryCode)
        {
            var digits = new string((countryCode ?? string.Empty).Where(char.IsDigit).ToArray());
            return string.IsNullOrWhiteSpace(digits) ? string.Empty : $"+{digits}";
        }

        private static bool TryNormalizeLocalCameroonPhoneNumber(string? phoneNumber, out string normalizedPhoneNumber)
        {
            normalizedPhoneNumber = string.Empty;
            var trimmed = (phoneNumber ?? string.Empty).Trim();
            if (trimmed.Length != 9 || trimmed.Any(character => !char.IsDigit(character)))
            {
                return false;
            }

            if (!trimmed.StartsWith("6", StringComparison.Ordinal))
            {
                return false;
            }

            normalizedPhoneNumber = trimmed;
            return true;
        }

        private static bool SamePhone(string? left, string? right)
        {
            var normalizedLeft = NormalizePhone(left);
            var normalizedRight = NormalizePhone(right);
            return normalizedLeft.Length > 0 &&
                   normalizedRight.Length > 0 &&
                   string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }

        private static string NormalizePhone(string? phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return string.Empty;
            }

            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }

            if (CameroonMobileMoneyNumberHelper.TryNormalizeNationalNumber(phoneNumber, out var cameroonMobileMoneyNumber))
            {
                return cameroonMobileMoneyNumber;
            }

            return digits;
        }

        private async Task TryRollbackRegistrationAsync(ApplicationUser? createdUser)
        {
            if (createdUser == null)
            {
                return;
            }

            try
            {
                var existing = await _userManager.FindByIdAsync(createdUser.Id);
                if (existing != null && !existing.EmailConfirmed)
                {
                    await _userManager.DeleteAsync(existing);
                }
            }
            catch (Exception rollbackEx)
            {
                _logger.LogWarning(rollbackEx,
                    "Failed to rollback partially-created registration for user {UserId}",
                    createdUser.Id);
            }
        }

        private ObjectResult OtpThrottled(OtpSendThrottledException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                Code = "OTP_REQUEST_LIMITED",
                Message = ex.Message,
                ex.Status.RetryAfterSeconds,
                ex.Status.DailyRequestLimit,
                ex.Status.DailyRequestsRemaining,
                ex.Status.DailyLimitReached,
                ex.Status.NextAllowedAt,
                ex.Status.DailyLimitResetsAt
            });
        }

        private ObjectResult ServerError(Exception ex, string operation, string userMessage)
        {
            _logger.LogError(ex, "AccountController failure in {Operation}", operation);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Code = "SERVER_ERROR",
                Message = userMessage
            });
        }

        private async Task<IActionResult?> ValidateOtpAsync(
            ApplicationUser user,
            string otpTokenName,
            string expiryTokenName,
            string providedOtp,
            string label)
        {
            var storedOtp = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, otpTokenName);
            var storedExpiry = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, expiryTokenName);

            if (string.IsNullOrWhiteSpace(storedOtp) || string.IsNullOrWhiteSpace(storedExpiry))
            {
                return BadRequest($"No valid {label} OTP found. Please request a new code.");
            }

            if (!long.TryParse(storedExpiry, out var expiryUnix))
            {
                return BadRequest($"Invalid {label} OTP state. Please request a new code.");
            }

            var expiryUtc = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
            if (expiryUtc <= DateTimeOffset.UtcNow)
            {
                return BadRequest($"{label[..1].ToUpperInvariant()}{label[1..]} OTP has expired. Please request a new code.");
            }

            if (!string.Equals(storedOtp, providedOtp, StringComparison.Ordinal))
            {
                return BadRequest($"Invalid {label} OTP.");
            }

            return null;
        }

        private async Task RemoveOtpAsync(ApplicationUser user, string otpTokenName, string expiryTokenName)
        {
            await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, otpTokenName);
            await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, expiryTokenName);
        }
    }
}







