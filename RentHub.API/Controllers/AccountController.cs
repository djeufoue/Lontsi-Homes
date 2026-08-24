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
using System.Net;
using System.Security.Claims;
using System.Linq;
using System.Text.Json;

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
        private const string MobilePaymentRollbackTokenPrefix = "MobilePaymentRollback:";
        private const string PlatformTermsVersion = "2026-06-kyc-v1";

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly TokenService _tokenService;
        private readonly IEmailService _emailService;
        private readonly IUserOnboardingService _userOnboardingService;
        private readonly IKycFileStorageService _kycFileStorageService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager,
            ApplicationDbContext context,
            TokenService tokenService,
            IEmailService emailService,
            IUserOnboardingService userOnboardingService,
            IKycFileStorageService kycFileStorageService,
            IConfiguration configuration,
            ILogger<AccountController> logger)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _tokenService = tokenService;
            _emailService = emailService;
            _userOnboardingService = userOnboardingService;
            _kycFileStorageService = kycFileStorageService;
            _configuration = configuration;
            _logger = logger;
        }

        private bool IsSmsVerificationEnabled =>
            _configuration.GetValue<bool?>("Onboarding:SmsVerificationEnabled").GetValueOrDefault(false);

        private bool IsStripeConnectPlatformEnabled =>
            _configuration.GetValue<bool?>("Stripe:Connect:Enabled").GetValueOrDefault(false);

        private bool IsStripePayoutSetupRequired =>
            _configuration.GetValue<bool?>("Stripe:Connect:RequirePayoutSetup") ?? IsStripeConnectPlatformEnabled;

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

                var normalizedPayoutPhone = string.Empty;
                if (!string.IsNullOrWhiteSpace(request.PayoutPhoneNumber) || request.PayoutChannel.HasValue)
                {
                    if (request.PayoutChannel is not PayoutChannelEnum.MtnMoney and not PayoutChannelEnum.OrangeMoney)
                    {
                        return BadRequest("Choose MTN Money or Orange Money when a payout number is provided.");
                    }

                    var payoutValidation = ValidateCameroonMobileMoneyNumber(
                        request.PayoutPhoneNumber,
                        request.PayoutChannel,
                        "payout number",
                        out normalizedPayoutPhone);
                    if (payoutValidation != null)
                    {
                        return payoutValidation;
                    }
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
                    WhatsAppPhoneNumber = request.WhatsAppPhoneNumber?.Trim(),
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
                    Message = "Email verified. Choose your country to continue."
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "VerifyLandlordEmail", "Unable to verify email OTP right now. Please try again.");
            }
        }

        [HttpPost("landlord-registration/country")]
        public async Task<IActionResult> UpsertLandlordCountry([FromBody] UpsertLandlordCountryRequest request)
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
                        Message = "Confirm your email before choosing your country."
                    });
                }

                var countryCode = NormalizeCountryCode(request.CountryCode);
                var countryIsoCode = NormalizeCountryIsoCode(request.CountryIsoCode) ?? ResolveCountryIsoFromCountryCode(countryCode);
                if (string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(countryIsoCode))
                {
                    return BadRequest(new
                    {
                        Code = "COUNTRY_REQUIRED",
                        Message = "Choose the country where your landlord account is registered."
                    });
                }

                var countryChanged = !string.Equals(
                    NormalizeCountryCode(user.CountryCode),
                    countryCode,
                    StringComparison.Ordinal) ||
                    !string.Equals(
                        NormalizeCountryIsoCode(user.CountryIsoCode),
                        countryIsoCode,
                        StringComparison.OrdinalIgnoreCase);

                user.CountryCode = countryCode;
                user.CountryIsoCode = countryIsoCode;

                if (countryChanged)
                {
                    user.PhoneNumberConfirmed = false;
                    user.PhoneNumber = null;

                    if (!IsCameroonCountry(countryIsoCode, countryCode))
                    {
                        ClearMobileMoneyFields(user);
                    }
                    else
                    {
                        user.UsePrimaryPhoneForSubscriptionPayments = true;
                        user.UsePrimaryPhoneForRentPayouts = true;
                    }
                }

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                var status = await BuildLandlordOnboardingStatusAsync(user);
                var nextStep = status.NextStep;

                return Ok(new
                {
                    Email = user.Email,
                    NextStep = nextStep,
                    Status = status,
                    Message = !status.SmsVerificationEnabled
                        ? "Country saved. Continue with identity verification."
                        : IsCameroonCountry(countryIsoCode, countryCode)
                        ? "Country saved. Verify your Cameroon phone number next."
                        : "Country saved. Verify your main phone number next."
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "UpsertLandlordCountry", "Unable to save your country right now. Please try again.");
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

                var countryCode = NormalizeCountryCode(request.CountryCode) ?? NormalizeCountryCode(user.CountryCode);
                if (string.IsNullOrWhiteSpace(countryCode))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "LANDLORD_ONBOARDING_INCOMPLETE",
                        Email = user.Email,
                        NextStep = LandlordOnboardingSteps.Country,
                        Message = "Choose your country before verifying your phone number."
                    });
                }

                string phone;
                if (IsCameroonCountryCode(countryCode))
                {
                    if (!TryNormalizeLocalCameroonPhoneNumber(request.PhoneNumber, out phone))
                    {
                        return BadRequest(new
                        {
                            Code = "PHONE_NUMBER_INVALID",
                            Message = "Enter the 9-digit Cameroon phone number without the country code. Example: REMOVED_PRIVATE_VALUE."
                        });
                    }
                }
                else if (!TryNormalizeLocalPhoneNumber(request.PhoneNumber, countryCode, out phone))
                {
                    return BadRequest(new
                    {
                        Code = "PHONE_NUMBER_INVALID",
                        Message = "Enter a valid phone number for the selected country."
                    });
                }

                var phoneChanged = !SamePhone(user.PhoneNumber, phone);

                user.CountryCode = countryCode;
                user.CountryIsoCode ??= ResolveCountryIsoFromCountryCode(countryCode);
                user.PhoneNumber = phone;

                if (phoneChanged)
                {
                    user.PhoneNumberConfirmed = false;

                    if (IsCameroonCountryCode(countryCode) && user.UsePrimaryPhoneForSubscriptionPayments)
                    {
                        user.SubscriptionPaymentPhoneNumber = phone;
                        user.IsSubscriptionPaymentPhoneVerified = false;
                        user.SubscriptionPaymentPhoneVerifiedAt = null;
                    }

                    if (IsCameroonCountryCode(countryCode) && user.UsePrimaryPhoneForRentPayouts)
                    {
                        user.PayoutPhoneNumber = phone;
                        user.IsPayoutPhoneVerified = false;
                        user.PayoutPhoneVerifiedAt = null;
                    }

                    if (!IsCameroonCountryCode(countryCode))
                    {
                        ClearMobileMoneyFields(user);
                    }
                }

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                if (!await IsLandlordPhoneVerificationEnabledAsync())
                {
                    var deferredStatus = await BuildLandlordOnboardingStatusAsync(user);
                    return Ok(new
                    {
                        Email = user.Email,
                        NextStep = deferredStatus.NextStep,
                        Status = deferredStatus,
                        Message = "Continue with identity verification."
                    });
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

                if (!await IsLandlordPhoneVerificationEnabledAsync())
                {
                    var deferredStatus = await BuildLandlordOnboardingStatusAsync(user);
                    return Ok(new
                    {
                        Email = user.Email,
                        NextStep = deferredStatus.NextStep,
                        Status = deferredStatus,
                        Message = "Continue with identity verification."
                    });
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
                    Message = IsCameroonCountryCode(user.CountryCode)
                        ? "Phone number verified. Configure mobile payment numbers next."
                        : "Phone number verified. Continue with identity verification."
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
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return Conflict(new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Mobile payment setup is skipped while automatic payments are disabled."
                    });
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                if (!await IsLandlordPhoneVerificationEnabledAsync())
                {
                    return Ok(await BuildMobilePaymentOtpResponseAsync(
                        user,
                        "Continue with identity verification."));
                }

                if (!user.EmailConfirmed || !user.PhoneNumberConfirmed || string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "LANDLORD_ONBOARDING_INCOMPLETE",
                        Email = user.Email,
                        NextStep = !user.EmailConfirmed
                            ? LandlordOnboardingSteps.Email
                            : string.IsNullOrWhiteSpace(user.CountryCode)
                                ? LandlordOnboardingSteps.Country
                                : LandlordOnboardingSteps.Phone,
                        Message = "Confirm your email and main phone number before configuring mobile payments."
                    });
                }

                if (!IsCameroonCountryCode(user.CountryCode))
                {
                    return BadRequest(new
                    {
                        Code = "MOBILE_MONEY_COUNTRY_NOT_SUPPORTED",
                        Message = "Mobile Money setup is only required for Cameroon landlord accounts. Use card payments for subscriptions."
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
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return Conflict(new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Mobile payment setup is unavailable while automatic payments are disabled."
                    });
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var user = await FindLandlordForOnboardingAsync(request.Email);
                if (user == null)
                {
                    return BadRequest("Invalid landlord account.");
                }

                if (!await IsLandlordPhoneVerificationEnabledAsync())
                {
                    return Ok(await BuildMobilePaymentOtpResponseAsync(
                        user,
                        "Continue with identity verification."));
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

        [HttpPost("mobile-payments/start-update")]
        [Authorize]
        public async Task<IActionResult> StartMobilePaymentNumberUpdate([FromBody] StartMobilePaymentNumberUpdateRequest request)
        {
            try
            {
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return Conflict(new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Mobile payment setup is unavailable while automatic payments are disabled."
                    });
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

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

                if (!await _userManager.IsInRoleAsync(user, "Landlord"))
                {
                    return Forbid();
                }

                if (!await IsLandlordPhoneVerificationEnabledAsync())
                {
                    return Ok(await BuildMobilePaymentOtpResponseAsync(
                        user,
                        "Continue with identity verification."));
                }

                if (!IsCameroonCountryCode(user.CountryCode))
                {
                    return BadRequest("Mobile Money setup is only available for Cameroon landlord accounts.");
                }

                if (HasPendingMobilePaymentVerification(user))
                {
                    return BadRequest("Complete the pending OTP validation before changing another Mobile Money number.");
                }

                var target = NormalizeMobilePaymentTarget(request.Target);
                if (target == null)
                {
                    return BadRequest("Choose a valid phone number to update.");
                }

                var updateRequest = BuildMobilePaymentsRequestFromUser(user);
                if (!CameroonMobileMoneyNumberHelper.TryNormalizeNationalNumber(request.PhoneNumber, out var newPhone))
                {
                    return BadRequest(new
                    {
                        Code = "PHONE_FORMAT_INVALID",
                        Message = $"Enter a valid Cameroon number with country code +237. {CameroonMobileMoneyNumberHelper.SupportedPrefixesDescription}"
                    });
                }

                switch (target)
                {
                    case "subscription":
                        if (request.Channel is not PayoutChannelEnum.MtnMoney and not PayoutChannelEnum.OrangeMoney)
                        {
                            return BadRequest("Choose MTN Money or Orange Money for subscription payments.");
                        }

                        if (SamePhone(newPhone, user.SubscriptionPaymentPhoneNumber))
                        {
                            return BadRequest("Enter a different subscription payment number before requesting an OTP.");
                        }

                        updateRequest.UsePrimaryPhoneForSubscriptionPayments = SamePhone(newPhone, user.PhoneNumber);
                        updateRequest.SubscriptionPaymentPhoneNumber = updateRequest.UsePrimaryPhoneForSubscriptionPayments
                            ? null
                            : newPhone;
                        updateRequest.SubscriptionPaymentChannel = request.Channel;
                        break;

                    case "payout":
                        if (request.Channel is not PayoutChannelEnum.MtnMoney and not PayoutChannelEnum.OrangeMoney)
                        {
                            return BadRequest("Choose MTN Money or Orange Money for rent payouts.");
                        }

                        if (SamePhone(newPhone, user.PayoutPhoneNumber))
                        {
                            return BadRequest("Enter a different rent payout number before requesting an OTP.");
                        }

                        updateRequest.UsePrimaryPhoneForRentPayouts = SamePhone(newPhone, user.PhoneNumber);
                        updateRequest.PayoutPhoneNumber = updateRequest.UsePrimaryPhoneForRentPayouts
                            ? null
                            : newPhone;
                        updateRequest.PayoutChannel = request.Channel;
                        break;

                    case "whatsapp":
                        if (SamePhone(newPhone, user.WhatsAppPhoneNumber))
                        {
                            return BadRequest("Enter a different WhatsApp alerts number before requesting an OTP.");
                        }

                        updateRequest.WhatsAppPhoneNumber = newPhone;
                        break;
                }

                await StoreMobilePaymentRollbackSnapshotAsync(user, target);
                return await UpsertLandlordMobilePayments(updateRequest);
            }
            catch (OtpSendThrottledException ex)
            {
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                return ServerError(ex, "StartMobilePaymentNumberUpdate", "Unable to send the OTP right now. Please try again.");
            }
        }

        [HttpPost("mobile-payments/cancel-update")]
        [Authorize]
        public async Task<IActionResult> CancelMobilePaymentNumberUpdate([FromBody] CancelMobilePaymentNumberUpdateRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

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

                if (!await _userManager.IsInRoleAsync(user, "Landlord"))
                {
                    return Forbid();
                }

                var target = NormalizeMobilePaymentTarget(request.Target);
                if (target == null)
                {
                    return BadRequest("Choose a valid phone number to discard.");
                }

                var snapshot = await ReadMobilePaymentRollbackSnapshotAsync(user, target);
                if (snapshot == null)
                {
                    return Ok(await BuildMobilePaymentOtpResponseAsync(
                        user,
                        "No pending Mobile Money update was found."));
                }

                RestoreMobilePaymentSnapshot(user, snapshot);

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                await RemoveMobilePaymentOtpForTargetAsync(user, target);
                await RemoveMobilePaymentRollbackSnapshotAsync(user, target);

                return Ok(await BuildMobilePaymentOtpResponseAsync(
                    user,
                    "Mobile Money update discarded. Previous number restored."));
            }
            catch (Exception ex)
            {
                return ServerError(ex, "CancelMobilePaymentNumberUpdate", "Unable to discard the Mobile Money update right now. Please try again.");
            }
        }

        [HttpPost("mobile-payments/verify-otp")]
        [Authorize]
        public async Task<IActionResult> VerifyMobilePaymentOtp([FromBody] VerifyMobilePaymentOtpRequest request)
        {
            try
            {
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return Conflict(new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Mobile payment verification is unavailable while automatic payments are disabled."
                    });
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

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

                if (!await _userManager.IsInRoleAsync(user, "Landlord"))
                {
                    return Forbid();
                }

                if (!await IsLandlordPhoneVerificationEnabledAsync())
                {
                    return Ok(await BuildMobilePaymentOtpResponseAsync(
                        user,
                        "Continue with identity verification."));
                }

                if (!IsCameroonCountryCode(user.CountryCode))
                {
                    return BadRequest("Mobile Money verification is only available for Cameroon landlord accounts.");
                }

                var target = (request.Target ?? string.Empty).Trim().ToLowerInvariant();
                var otp = request.Otp?.Trim() ?? string.Empty;
                var verifiedAt = DateTimeOffset.UtcNow;
                string targetLabel;

                switch (target)
                {
                    case "subscription":
                    case "subscription-payment":
                        if (string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber) || user.SubscriptionPaymentChannel == null)
                        {
                            return BadRequest("Configure a subscription payment number before verifying it.");
                        }

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
                                    otp,
                                    "subscription payment number");

                                if (otpResult != null)
                                {
                                    return otpResult;
                                }

                                user.IsSubscriptionPaymentPhoneVerified = true;
                                user.SubscriptionPaymentPhoneVerifiedAt = verifiedAt;
                            }
                        }

                        if (!user.IsPayoutPhoneVerified &&
                            SamePhone(user.PayoutPhoneNumber, user.SubscriptionPaymentPhoneNumber))
                        {
                            user.IsPayoutPhoneVerified = true;
                            user.PayoutPhoneVerifiedAt = verifiedAt;
                        }

                        targetLabel = "Subscription payment number";
                        break;

                    case "payout":
                    case "rent-payout":
                        if (string.IsNullOrWhiteSpace(user.PayoutPhoneNumber) || user.PayoutChannel == null)
                        {
                            return BadRequest("Configure a rent payout number before verifying it.");
                        }

                        if (!user.IsPayoutPhoneVerified)
                        {
                            if (SamePhone(user.PayoutPhoneNumber, user.PhoneNumber) && user.PhoneNumberConfirmed ||
                                SamePhone(user.PayoutPhoneNumber, user.SubscriptionPaymentPhoneNumber) && user.IsSubscriptionPaymentPhoneVerified)
                            {
                                user.IsPayoutPhoneVerified = true;
                                user.PayoutPhoneVerifiedAt = verifiedAt;
                            }
                            else if (SamePhone(user.PayoutPhoneNumber, user.SubscriptionPaymentPhoneNumber) &&
                                     !user.IsSubscriptionPaymentPhoneVerified)
                            {
                                return BadRequest("Verify the subscription payment number first. The rent payout uses the same pending number.");
                            }
                            else
                            {
                                var otpResult = await ValidateOtpAsync(
                                    user,
                                    PayoutOtpTokenName,
                                    PayoutOtpExpiryTokenName,
                                    otp,
                                    "rent payout number");

                                if (otpResult != null)
                                {
                                    return otpResult;
                                }

                                user.IsPayoutPhoneVerified = true;
                                user.PayoutPhoneVerifiedAt = verifiedAt;
                            }
                        }

                        targetLabel = "Rent payout number";
                        break;

                    case "whatsapp":
                        if (string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
                        {
                            return BadRequest("Configure a WhatsApp alerts number before verifying it.");
                        }

                        if (!user.IsWhatsAppPhoneVerified)
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
                                    otp,
                                    "WhatsApp");

                                if (otpResult != null)
                                {
                                    return otpResult;
                                }

                                user.IsWhatsAppPhoneVerified = true;
                                user.WhatsAppPhoneVerifiedAt = verifiedAt;
                            }
                        }

                        targetLabel = "WhatsApp alerts number";
                        break;

                    default:
                        return BadRequest("Choose a valid mobile payment number to verify.");
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

                var verifiedTarget = NormalizeMobilePaymentTarget(request.Target);
                if (verifiedTarget != null)
                {
                    await RemoveMobilePaymentRollbackSnapshotAsync(user, verifiedTarget);
                }

                return Ok(await BuildMobilePaymentOtpResponseAsync(
                    user,
                    $"{targetLabel} verified successfully."));
            }
            catch (Exception ex)
            {
                return ServerError(ex, "VerifyMobilePaymentOtp", "Unable to verify the OTP right now. Please try again.");
            }
        }

        [HttpPost("mobile-payments/resend-otp")]
        [Authorize]
        public async Task<IActionResult> ResendMobilePaymentOtp([FromBody] ResendMobilePaymentOtpRequest request)
        {
            try
            {
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return Conflict(new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Mobile payment verification is unavailable while automatic payments are disabled."
                    });
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

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

                if (!await _userManager.IsInRoleAsync(user, "Landlord"))
                {
                    return Forbid();
                }

                if (!await IsLandlordPhoneVerificationEnabledAsync())
                {
                    return Ok(await BuildMobilePaymentOtpResponseAsync(
                        user,
                        "Continue with identity verification."));
                }

                if (!IsCameroonCountryCode(user.CountryCode))
                {
                    return BadRequest("Mobile Money OTP resend is only available for Cameroon landlord accounts.");
                }

                var target = (request.Target ?? string.Empty).Trim().ToLowerInvariant();
                var sendSubscriptionOtp = false;
                var sendPayoutOtp = false;
                var sendWhatsAppOtp = false;
                var targetLabel = string.Empty;

                switch (target)
                {
                    case "subscription":
                    case "subscription-payment":
                        if (string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber) || user.SubscriptionPaymentChannel == null)
                        {
                            return BadRequest("Configure a subscription payment number before requesting an OTP.");
                        }

                        if (user.IsSubscriptionPaymentPhoneVerified)
                        {
                            return Ok(await BuildMobilePaymentOtpResponseAsync(
                                user,
                                "Subscription payment number is already verified."));
                        }

                        sendSubscriptionOtp = true;
                        targetLabel = "subscription payment number";
                        break;

                    case "payout":
                    case "rent-payout":
                        if (string.IsNullOrWhiteSpace(user.PayoutPhoneNumber) || user.PayoutChannel == null)
                        {
                            return BadRequest("Configure a rent payout number before requesting an OTP.");
                        }

                        if (user.IsPayoutPhoneVerified)
                        {
                            return Ok(await BuildMobilePaymentOtpResponseAsync(
                                user,
                                "Rent payout number is already verified."));
                        }

                        if (SamePhone(user.PayoutPhoneNumber, user.SubscriptionPaymentPhoneNumber) &&
                            !user.IsSubscriptionPaymentPhoneVerified)
                        {
                            return BadRequest("Verify the subscription payment number first. The rent payout uses the same pending number.");
                        }

                        sendPayoutOtp = true;
                        targetLabel = "rent payout number";
                        break;

                    case "whatsapp":
                        if (string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
                        {
                            return BadRequest("Configure a WhatsApp alerts number before requesting an OTP.");
                        }

                        if (user.IsWhatsAppPhoneVerified)
                        {
                            return Ok(await BuildMobilePaymentOtpResponseAsync(
                                user,
                                "WhatsApp alerts number is already verified."));
                        }

                        sendWhatsAppOtp = true;
                        targetLabel = "WhatsApp alerts number";
                        break;

                    default:
                        return BadRequest("Choose a valid mobile payment number to resend an OTP.");
                }

                await _userOnboardingService.SendLandlordMobilePaymentOtpsAsync(
                    user,
                    sendSubscriptionOtp,
                    sendPayoutOtp,
                    sendWhatsAppOtp);

                return Ok(await BuildMobilePaymentOtpResponseAsync(
                    user,
                    $"OTP sent to your {targetLabel}."));
            }
            catch (OtpSendThrottledException ex)
            {
                return OtpThrottled(ex);
            }
            catch (Exception ex)
            {
                return ServerError(ex, "ResendMobilePaymentOtp", "Unable to resend the OTP right now. Please try again.");
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
                    or LandlordOnboardingSteps.Country
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
                await SendKycSubmittedAdminEmailAsync(user, profile);

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
                    PhoneNumber = request.PhoneNumber?.Trim(),
                    WhatsAppPhoneNumber = request.WhatsAppPhoneNumber?.Trim(),
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

                var roles = await _userManager.GetRolesAsync(user);
                var requiresAllContactOtps = user.SessionInvalidatedAt.HasValue;
                var requiresPhoneActivationOtps = requiresAllContactOtps ||
                    roles.Contains("Landlord", StringComparer.OrdinalIgnoreCase);

                if (requiresAllContactOtps && !string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    var phoneOtpResult = await ValidateOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName, request.PhoneOtp?.Trim() ?? string.Empty, "phone number");
                    if (phoneOtpResult != null)
                    {
                        return phoneOtpResult;
                    }
                }

                if (requiresAllContactOtps && !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
                {
                    var subscriptionOtpResult = await ValidateOtpAsync(user, SubscriptionPaymentOtpTokenName, SubscriptionPaymentOtpExpiryTokenName, request.SubscriptionPaymentOtp?.Trim() ?? string.Empty, "subscription payment number");
                    if (subscriptionOtpResult != null)
                    {
                        return subscriptionOtpResult;
                    }
                }

                if (requiresPhoneActivationOtps && !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
                {
                    var payoutOtpResult = await ValidateOtpAsync(user, PayoutOtpTokenName, PayoutOtpExpiryTokenName, request.PayoutOtp?.Trim() ?? string.Empty, "payout number");
                    if (payoutOtpResult != null)
                    {
                        return payoutOtpResult;
                    }
                }

                if (requiresPhoneActivationOtps && !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
                {
                    var whatsAppOtpResult = await ValidateOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName, request.WhatsAppOtp?.Trim() ?? string.Empty, "WhatsApp");
                    if (whatsAppOtpResult != null)
                    {
                        return whatsAppOtpResult;
                    }
                }

                user.EmailConfirmed = true;
                if (requiresAllContactOtps && !string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    user.PhoneNumberConfirmed = true;
                }

                if (requiresAllContactOtps && !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
                {
                    user.IsSubscriptionPaymentPhoneVerified = true;
                    user.SubscriptionPaymentPhoneVerifiedAt = DateTimeOffset.UtcNow;
                }
                if (requiresPhoneActivationOtps && !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
                {
                    user.IsPayoutPhoneVerified = true;
                    user.PayoutPhoneVerifiedAt = DateTimeOffset.UtcNow;
                }

                if (requiresPhoneActivationOtps && !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
                {
                    if (SamePhone(user.SubscriptionPaymentPhoneNumber, user.PayoutPhoneNumber) && user.IsPayoutPhoneVerified)
                    {
                        user.IsSubscriptionPaymentPhoneVerified = true;
                        user.SubscriptionPaymentPhoneVerifiedAt = DateTimeOffset.UtcNow;
                    }
                }

                if (requiresPhoneActivationOtps && !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
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
                await RemoveOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName);
                await RemoveOtpAsync(user, SubscriptionPaymentOtpTokenName, SubscriptionPaymentOtpExpiryTokenName);
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

                if (!string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    var phoneOtpResult = await ValidateOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName, request.PhoneOtp?.Trim() ?? string.Empty, "phone number");
                    if (phoneOtpResult != null)
                    {
                        return phoneOtpResult;
                    }
                }

                if (!string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
                {
                    var whatsAppOtpResult = await ValidateOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName, request.WhatsAppOtp?.Trim() ?? string.Empty, "WhatsApp");
                    if (whatsAppOtpResult != null)
                    {
                        return whatsAppOtpResult;
                    }
                }

                user.EmailConfirmed = true;
                user.PhoneNumberConfirmed = !string.IsNullOrWhiteSpace(user.PhoneNumber);
                user.IsWhatsAppPhoneVerified = !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber);
                user.WhatsAppPhoneVerifiedAt = user.IsWhatsAppPhoneVerified ? DateTimeOffset.UtcNow : null;

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

                if (user.SessionInvalidatedAt.HasValue)
                {
                    await _userOnboardingService.SendEmailChangeVerificationOtpAsync(user);
                }
                else
                {
                    await _userOnboardingService.SendActivationOtpAsync(user);
                }
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

                var roles = (await _tokenService.GetEffectiveRolesAsync(user)).ToList();
                var isAdmin = roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
                var isVisitor = roles.Any(r => string.Equals(r, "Visitor", StringComparison.OrdinalIgnoreCase));
                var isLandlord = roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase));
                var isTenant = roles.Any(r => string.Equals(r, "Tenant", StringComparison.OrdinalIgnoreCase));

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
                            : isTenant
                                ? "Tenant account is not activated. Verify the OTP code sent to your email."
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
                else if (!isLandlord && !isTenant)
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
        public async Task<IActionResult> GetProfileOverview(
            [FromQuery(Name = "userId")] string? requestedUserId = null,
            [FromQuery] int? tenancyId = null)
        {
            try
            {
                var currentUserId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Unauthorized();
                }

                var userId = string.IsNullOrWhiteSpace(requestedUserId)
                    ? currentUserId
                    : requestedUserId.Trim();
                if (!string.Equals(currentUserId, userId, StringComparison.Ordinal) &&
                    !await CanAccessTargetProfileAsync(currentUserId, userId, tenancyId))
                {
                    return Forbid();
                }

                var user = await _userManager.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null)
                {
                    return NotFound("User was not found.");
                }

                var roles = (await _tokenService.GetEffectiveRolesAsync(user)).ToList();
                var isAdmin = roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
                var now = DateTimeOffset.UtcNow;

                var ownedPropertyIds = await _context.Properties
                    .Where(p => !p.IsDeleted && p.LandlordId == userId)
                    .Select(p => p.Id)
                    .ToListAsync();

                var managedPropertyIds = await _context.PropertyManagerAssignments
                    .Where(m => !m.IsDeleted && m.ManagerId == userId)
                    .Select(m => m.PropertyId)
                    .ToListAsync();

                var ownerPropertyIds = await _context.ApartmentOwners
                    .Where(o => !o.IsDeleted && o.OwnerId == userId)
                    .Join(_context.Apartments, o => o.ApartmentId, a => a.Id, (o, a) => a.PropertyId)
                    .Distinct()
                    .ToListAsync();

                var tenantPropertyIds = await _context.Tenancies
                    .Where(t =>
                        !t.IsDeleted &&
                        t.Members.Any(m => !m.IsDeleted && m.MemberId == userId))
                    .Join(_context.Apartments, t => t.ApartmentId, a => a.Id, (t, a) => a.PropertyId)
                    .Distinct()
                    .ToListAsync();

                List<int> accessiblePropertyIds;
                if (isAdmin)
                {
                    accessiblePropertyIds = await _context.Properties
                        .Where(p => !p.IsDeleted)
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
                    .Where(p => !p.IsDeleted && accessiblePropertyIds.Contains(p.Id))
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
                        !(us.PaymentMethod == PaymentMethodEnum.Card &&
                          us.PaymentStatus == PaymentStatusEnum.Pending) &&
                        us.EndDate > now)
                    .OrderByDescending(us => us.UpdatedAt ?? us.CreatedAt)
                    .FirstOrDefaultAsync();

                var landlordStatus = roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase))
                    ? await BuildLandlordOnboardingStatusAsync(user, roles)
                    : null;
                var automaticPaymentsEnabled = await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context);
                var stripePayoutSetupRequired = automaticPaymentsEnabled && IsStripePayoutSetupRequired;

                var plans = await _context.SubscriptionPlans
                    .OrderBy(p => p.DisplayOrder)
                    .ThenBy(p => p.Price)
                    .Select(p => new SubscriptionPlanDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Price = p.Price,
                        AnnualPrice = p.AnnualPrice,
                        DurationInDays = p.DurationInDays,
                        Description = p.Description ?? string.Empty,
                        MaxProperties = p.MaxProperties,
                        MaxApartmentsPerProperty = p.MaxApartmentsPerProperty,
                        MaxTotalApartments = p.MaxTotalApartments,
                        AudienceLabel = p.AudienceLabel ?? string.Empty,
                        FeatureHighlights = p.FeatureHighlights ?? string.Empty,
                        IsRecommended = p.IsRecommended,
                        IsContactSales = p.IsContactSales,
                        DisplayOrder = p.DisplayOrder
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
                    CountryIsoCode = NormalizeCountryIsoCode(user.CountryIsoCode) ?? ResolveCountryIsoFromCountryCode(user.CountryCode),
                    PhoneNumber = user.PhoneNumber,
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
                    SubscriptionPaymentOtpRequestLimit = landlordStatus?.SubscriptionPaymentOtpRequestLimit,
                    PayoutOtpRequestLimit = landlordStatus?.PayoutOtpRequestLimit,
                    WhatsAppOtpRequestLimit = landlordStatus?.WhatsAppOtpRequestLimit,
                    SmsVerificationEnabled = landlordStatus?.SmsVerificationEnabled ?? IsSmsVerificationEnabled,
                    Roles = roles.ToList(),
                    IsSubscriptionExempt = user.IsSubscriptionExempt,
                    Language = user.Language,
                    EmailLanguage = user.EmailLanguage,
                    ConversationEmailNotificationsEnabled = user.ConversationEmailNotificationsEnabled,
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
                    HasStripePayoutAccount = !string.IsNullOrWhiteSpace(user.StripeConnectAccountId),
                    StripePayoutSetupStarted = !string.IsNullOrWhiteSpace(user.StripeConnectAccountId) || user.StripePayoutSetupStartedAt.HasValue,
                    StripePayoutSetupComplete = IsStripePayoutSetupComplete(user),
                    StripeConnectPlatformEnabled = IsStripeConnectPlatformEnabled,
                    StripePayoutSetupRequired = stripePayoutSetupRequired,
                    AutomaticPaymentsEnabled = automaticPaymentsEnabled,
                    StripeConnectAccountId = user.StripeConnectAccountId ?? string.Empty,
                    StripePayoutDetailsSubmitted = user.StripePayoutDetailsSubmitted,
                    StripeChargesEnabled = user.StripeChargesEnabled,
                    StripePayoutsEnabled = user.StripePayoutsEnabled,
                    StripePayoutRequirementsSummary = user.StripePayoutRequirementsSummary ?? string.Empty,
                    StripePayoutDisabledReason = user.StripePayoutDisabledReason ?? string.Empty,
                    StripePayoutSetupStartedAt = user.StripePayoutSetupStartedAt,
                    StripePayoutSetupCompletedAt = user.StripePayoutSetupCompletedAt,
                    StripePayoutStatusUpdatedAt = user.StripePayoutStatusUpdatedAt,
                    NextOnboardingStep = landlordStatus?.NextStep ?? LandlordOnboardingSteps.Complete,
                    CanStartSubscriptionCheckout = CanStartSubscriptionCheckout(roles, landlordStatus, user, stripePayoutSetupRequired),
                    SubscriptionBlockedReason = ResolveSubscriptionBlockedReason(roles, landlordStatus, user, stripePayoutSetupRequired),
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

        [HttpPost("language")]
        [Authorize]
        public async Task<IActionResult> UpdateLanguage([FromBody] UpdatePlatformLanguageRequest request)
        {
            if (!PlatformLanguageOptions.IsSupported(request.Language))
            {
                return BadRequest(new { Message = "Select a supported language." });
            }

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

                user.Language = request.Language;
                var result = await _userManager.UpdateAsync(user);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                return Ok(new UpdatePlatformLanguageResponse
                {
                    Language = user.Language,
                    CultureName = user.Language.ToCultureName(),
                    Token = await _tokenService.GenerateTokenAsync(user)
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "UpdateLanguage", "Unable to update your language preference right now. Please try again.");
            }
        }

        [HttpPost("email-language")]
        [Authorize]
        public async Task<IActionResult> UpdateEmailLanguage([FromBody] UpdateEmailLanguageRequest request)
        {
            if (!PlatformLanguageOptions.IsSupported(request.EmailLanguage))
            {
                return BadRequest(new { Message = "Select a supported email language." });
            }

            try
            {
                var currentUserId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Unauthorized();
                }

                var targetUserId = string.IsNullOrWhiteSpace(request.UserId)
                    ? currentUserId
                    : request.UserId.Trim();
                if (!string.Equals(currentUserId, targetUserId, StringComparison.Ordinal) &&
                    !await CanAccessTargetProfileAsync(currentUserId, targetUserId, request.TenancyId))
                {
                    return Forbid();
                }

                var user = await _userManager.FindByIdAsync(targetUserId);
                if (user == null)
                {
                    return NotFound("User was not found.");
                }

                user.EmailLanguage = request.EmailLanguage;
                var result = await _userManager.UpdateAsync(user);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                return Ok(new UpdateEmailLanguageResponse
                {
                    EmailLanguage = user.EmailLanguage
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "UpdateEmailLanguage", "Unable to update your email language preference right now. Please try again.");
            }
        }

        [HttpPost("conversation-email-notifications")]
        [Authorize]
        public async Task<IActionResult> UpdateConversationEmailNotifications(
            [FromBody] UpdateConversationEmailNotificationsRequest request)
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

                user.ConversationEmailNotificationsEnabled = request.Enabled;
                var result = await _userManager.UpdateAsync(user);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                return Ok(new UpdateConversationEmailNotificationsResponse
                {
                    Enabled = user.ConversationEmailNotificationsEnabled
                });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "UpdateConversationEmailNotifications", "Unable to update your message email notification preference right now. Please try again.");
            }
        }

        private async Task<bool> CanAccessTargetProfileAsync(
            string currentUserId,
            string targetUserId,
            int? tenancyId)
        {
            if (string.Equals(currentUserId, targetUserId, StringComparison.Ordinal) || User.IsInRole("Admin"))
            {
                return true;
            }

            if (!tenancyId.HasValue)
            {
                return false;
            }

            var tenancy = await _context.Tenancies
                .AsNoTracking()
                .Where(item =>
                    !item.IsDeleted &&
                    item.Id == tenancyId.Value &&
                    item.Members.Any(member => !member.IsDeleted && member.MemberId == targetUserId))
                .Select(item => new
                {
                    item.Id,
                    item.ApartmentId,
                    PropertyId = item.Apartment!.PropertyId
                })
                .FirstOrDefaultAsync();

            return tenancy != null && await PropertyHelpers.CanAccessTenancyAsync(
                _context,
                tenancy.Id,
                tenancy.ApartmentId,
                tenancy.PropertyId,
                currentUserId,
                false);
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
        /// Emails a one-time password recovery link when the supplied address belongs to an account.
        /// The response is deliberately identical for known and unknown addresses.
        /// </summary>
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            const string responseMessage = "If an account exists for that email, a password reset link has been sent.";

            try
            {
                var email = request.Email.Trim();
                var user = await _userManager.FindByEmailAsync(email);
                if (user == null || string.IsNullOrWhiteSpace(user.Email))
                {
                    return Ok(new { Message = responseMessage });
                }

                var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
                if (!Uri.TryCreate(portalBaseUrl, UriKind.Absolute, out var portalUri) ||
                    (portalUri.Scheme != Uri.UriSchemeHttps && portalUri.Scheme != Uri.UriSchemeHttp))
                {
                    _logger.LogError("Password reset email was not sent because Portal:BaseUrl is missing or invalid.");
                    return Ok(new { Message = responseMessage });
                }

                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var resetUrl = $"{portalBaseUrl}/Auth/ResetPassword?email={Uri.EscapeDataString(user.Email)}&token={Uri.EscapeDataString(token)}";
                var encodedResetUrl = WebUtility.HtmlEncode(resetUrl);
                var isFrench = user.EmailLanguage == PlatformLanguage.French;

                var message = new EmailMessage
                {
                    To = user.Email,
                    Subject = isFrench
                        ? "Réinitialisez votre mot de passe Lontsi Homes"
                        : "Reset your Lontsi Homes password",
                    PlainTextBody = isFrench
                        ? string.Join(Environment.NewLine,
                            "Nous avons reçu une demande de réinitialisation de votre mot de passe Lontsi Homes.",
                            string.Empty,
                            "Choisissez un nouveau mot de passe à l’aide de ce lien :",
                            resetUrl,
                            string.Empty,
                            "Ce lien est à usage unique et expirera. Si vous n’avez pas demandé cette modification, vous pouvez ignorer ce courriel.")
                        : string.Join(Environment.NewLine,
                            "We received a request to reset your Lontsi Homes password.",
                            string.Empty,
                            "Choose a new password using this link:",
                            resetUrl,
                            string.Empty,
                            "This link can only be used once and will expire. If you did not request this change, you can ignore this email."),
                    HtmlBody = isFrench
                        ? $"<p>Nous avons reçu une demande de réinitialisation de votre mot de passe Lontsi Homes.</p><p><a href=\"{encodedResetUrl}\" style=\"display:inline-block;padding:12px 20px;border-radius:10px;background:#9da85e;color:#1f2522;font-weight:700;text-decoration:none;\">Choisir un nouveau mot de passe</a></p><p>Ce lien est à usage unique et expirera. Si vous n’avez pas demandé cette modification, vous pouvez ignorer ce courriel.</p>"
                        : $"<p>We received a request to reset your Lontsi Homes password.</p><p><a href=\"{encodedResetUrl}\" style=\"display:inline-block;padding:12px 20px;border-radius:10px;background:#9da85e;color:#1f2522;font-weight:700;text-decoration:none;\">Choose a new password</a></p><p>This link can only be used once and will expire. If you did not request this change, you can ignore this email.</p>"
                };

                var delivery = await _emailService.TrySendEmailAsync(message);
                if (!delivery.Succeeded)
                {
                    _logger.LogWarning("Password reset email delivery failed for user {UserId}.", user.Id);
                }

                return Ok(new { Message = responseMessage });
            }
            catch (Exception ex)
            {
                // A generic success response prevents account discovery and avoids exposing
                // transient provider failures to anonymous callers.
                _logger.LogError(ex, "ForgotPassword failed while processing a password reset request.");
                return Ok(new { Message = responseMessage });
            }
        }

        /// <summary>
        /// Resets an existing account password using the token sent by ForgotPassword.
        /// </summary>
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
            => await ResetPasswordInternal(request, confirmEmail: false);

        /// <summary>
        /// Sets the initial password for an invited account and confirms its invitation email.
        /// </summary>
        [HttpPost("set-invited-password")]
        public async Task<IActionResult> SetInvitedPassword([FromBody] ResetPasswordRequest request)
            => await ResetPasswordInternal(request, confirmEmail: true);

        private async Task<IActionResult> ResetPasswordInternal(ResetPasswordRequest request, bool confirmEmail)
        {
            try
            {
                var user = await _userManager.FindByEmailAsync(request.Email.Trim());
                if (user == null)
                {
                    return BadRequest(new { Message = "This password reset link is invalid or has expired." });
                }

                var wasEmailConfirmed = user.EmailConfirmed;
                if (confirmEmail)
                {
                    // ResetPasswordAsync persists the whole user, keeping password creation and
                    // invitation confirmation in a single Identity update.
                    user.EmailConfirmed = true;
                }

                var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
                if (!result.Succeeded)
                {
                    user.EmailConfirmed = wasEmailConfirmed;
                    return BadRequest(new { Message = "This password reset link is invalid or has expired." });
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
                CountryIsoCode = NormalizeCountryIsoCode(user.CountryIsoCode) ?? ResolveCountryIsoFromCountryCode(user.CountryCode),
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
                SmsVerificationEnabled = IsSmsVerificationEnabled &&
                    !await PaymentAvailabilityHelper.ShouldSkipLandlordPhoneVerificationAsync(_context),
                CreatedAt = user.CreatedAt,
                Roles = roles
            };

            status.PhoneOtpRequestLimit = await BuildOtpRequestLimitAsync(user, OtpSendPurposes.LandlordPhone);
            status.SubscriptionPaymentOtpRequestLimit = await BuildOtpRequestLimitAsync(user, OtpSendPurposes.SubscriptionPaymentPhone);
            status.PayoutOtpRequestLimit = await BuildOtpRequestLimitAsync(user, OtpSendPurposes.RentPayoutPhone);
            status.WhatsAppOtpRequestLimit = await BuildOtpRequestLimitAsync(user, OtpSendPurposes.WhatsAppPhone);

            var automaticPaymentsEnabled = await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context);
            status.NextStep = ResolveLandlordOnboardingStep(status, automaticPaymentsEnabled);
            status.IsComplete = status.NextStep == LandlordOnboardingSteps.Complete;
            return status;
        }

        private async Task<bool> IsLandlordPhoneVerificationEnabledAsync()
        {
            return IsSmsVerificationEnabled &&
                !await PaymentAvailabilityHelper.ShouldSkipLandlordPhoneVerificationAsync(_context);
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

        private async Task<object> BuildMobilePaymentOtpResponseAsync(ApplicationUser user, string message)
        {
            var status = await BuildLandlordOnboardingStatusAsync(user);
            return new
            {
                Email = user.Email,
                NextStep = status.NextStep,
                Status = status,
                Message = message
            };
        }

        private static UpsertLandlordMobilePaymentsRequest BuildMobilePaymentsRequestFromUser(ApplicationUser user)
        {
            return new UpsertLandlordMobilePaymentsRequest
            {
                Email = user.Email ?? string.Empty,
                UsePrimaryPhoneForSubscriptionPayments = user.UsePrimaryPhoneForSubscriptionPayments,
                SubscriptionPaymentPhoneNumber = user.SubscriptionPaymentPhoneNumber ?? user.PhoneNumber,
                SubscriptionPaymentChannel = user.SubscriptionPaymentChannel ?? PayoutChannelEnum.MtnMoney,
                UsePrimaryPhoneForRentPayouts = user.UsePrimaryPhoneForRentPayouts,
                PayoutPhoneNumber = user.PayoutPhoneNumber ?? user.PhoneNumber,
                PayoutChannel = user.PayoutChannel ?? PayoutChannelEnum.MtnMoney,
                WhatsAppPhoneNumber = user.WhatsAppPhoneNumber
            };
        }

        private static bool HasPendingMobilePaymentVerification(ApplicationUser user)
        {
            return !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber) && !user.IsSubscriptionPaymentPhoneVerified ||
                   !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber) && !user.IsPayoutPhoneVerified ||
                   !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) && !user.IsWhatsAppPhoneVerified;
        }

        private static string? NormalizeMobilePaymentTarget(string? target)
        {
            return (target ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "subscription" or "subscription-payment" => "subscription",
                "payout" or "rent-payout" => "payout",
                "whatsapp" => "whatsapp",
                _ => null
            };
        }

        private async Task StoreMobilePaymentRollbackSnapshotAsync(ApplicationUser user, string target)
        {
            var snapshot = target switch
            {
                "payout" => new MobilePaymentRollbackSnapshot
                {
                    Target = target,
                    UsePrimaryPhone = user.UsePrimaryPhoneForRentPayouts,
                    PhoneNumber = user.PayoutPhoneNumber,
                    Channel = user.PayoutChannel,
                    IsVerified = user.IsPayoutPhoneVerified,
                    VerifiedAt = user.PayoutPhoneVerifiedAt
                },
                "whatsapp" => new MobilePaymentRollbackSnapshot
                {
                    Target = target,
                    PhoneNumber = user.WhatsAppPhoneNumber,
                    IsVerified = user.IsWhatsAppPhoneVerified,
                    VerifiedAt = user.WhatsAppPhoneVerifiedAt
                },
                _ => new MobilePaymentRollbackSnapshot
                {
                    Target = "subscription",
                    UsePrimaryPhone = user.UsePrimaryPhoneForSubscriptionPayments,
                    PhoneNumber = user.SubscriptionPaymentPhoneNumber,
                    Channel = user.SubscriptionPaymentChannel,
                    IsVerified = user.IsSubscriptionPaymentPhoneVerified,
                    VerifiedAt = user.SubscriptionPaymentPhoneVerifiedAt
                }
            };

            await _userManager.SetAuthenticationTokenAsync(
                user,
                OtpLoginProvider,
                MobilePaymentRollbackTokenName(target),
                JsonSerializer.Serialize(snapshot));
        }

        private async Task<MobilePaymentRollbackSnapshot?> ReadMobilePaymentRollbackSnapshotAsync(
            ApplicationUser user,
            string target)
        {
            var raw = await _userManager.GetAuthenticationTokenAsync(
                user,
                OtpLoginProvider,
                MobilePaymentRollbackTokenName(target));

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<MobilePaymentRollbackSnapshot>(raw);
            }
            catch
            {
                return null;
            }
        }

        private Task RemoveMobilePaymentRollbackSnapshotAsync(ApplicationUser user, string target)
        {
            return _userManager.RemoveAuthenticationTokenAsync(
                user,
                OtpLoginProvider,
                MobilePaymentRollbackTokenName(target));
        }

        private static string MobilePaymentRollbackTokenName(string target)
        {
            return $"{MobilePaymentRollbackTokenPrefix}{target}";
        }

        private static void RestoreMobilePaymentSnapshot(
            ApplicationUser user,
            MobilePaymentRollbackSnapshot snapshot)
        {
            switch (snapshot.Target)
            {
                case "payout":
                    user.UsePrimaryPhoneForRentPayouts = snapshot.UsePrimaryPhone;
                    user.PayoutPhoneNumber = snapshot.PhoneNumber;
                    user.PayoutChannel = snapshot.Channel;
                    user.IsPayoutPhoneVerified = snapshot.IsVerified;
                    user.PayoutPhoneVerifiedAt = snapshot.VerifiedAt;
                    break;

                case "whatsapp":
                    user.WhatsAppPhoneNumber = snapshot.PhoneNumber;
                    user.IsWhatsAppPhoneVerified = snapshot.IsVerified;
                    user.WhatsAppPhoneVerifiedAt = snapshot.VerifiedAt;
                    break;

                default:
                    user.UsePrimaryPhoneForSubscriptionPayments = snapshot.UsePrimaryPhone;
                    user.SubscriptionPaymentPhoneNumber = snapshot.PhoneNumber;
                    user.SubscriptionPaymentChannel = snapshot.Channel;
                    user.IsSubscriptionPaymentPhoneVerified = snapshot.IsVerified;
                    user.SubscriptionPaymentPhoneVerifiedAt = snapshot.VerifiedAt;
                    break;
            }
        }

        private Task RemoveMobilePaymentOtpForTargetAsync(ApplicationUser user, string target)
        {
            return target switch
            {
                "payout" => RemoveOtpAsync(user, PayoutOtpTokenName, PayoutOtpExpiryTokenName),
                "whatsapp" => RemoveOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName),
                _ => RemoveOtpAsync(user, SubscriptionPaymentOtpTokenName, SubscriptionPaymentOtpExpiryTokenName)
            };
        }

        private static string ResolveLandlordOnboardingStep(
            LandlordOnboardingStatusDto status,
            bool automaticPaymentsEnabled)
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

            if (string.IsNullOrWhiteSpace(status.CountryCode))
            {
                return LandlordOnboardingSteps.Country;
            }

            if (!status.SmsVerificationEnabled)
            {
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

            if (string.IsNullOrWhiteSpace(status.PhoneNumber) || !status.PhoneNumberConfirmed)
            {
                return LandlordOnboardingSteps.Phone;
            }

            if (!automaticPaymentsEnabled)
            {
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

            if (!IsCameroonCountry(status.CountryIsoCode, status.CountryCode))
            {
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
                CountryCode = status.CountryCode,
                CountryIsoCode = status.CountryIsoCode,
                PhoneNumber = status.PhoneNumber,
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
                SmsVerificationEnabled = status.SmsVerificationEnabled,
                NextStep = status.NextStep,
                IsComplete = status.IsComplete,
                CreatedAt = status.CreatedAt
            };
        }

        private static bool CanStartSubscriptionCheckout(
            IReadOnlyCollection<string> roles,
            LandlordOnboardingStatusDto? landlordStatus,
            ApplicationUser user,
            bool requireStripePayoutSetup)
        {
            if (!roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return landlordStatus?.IsKycApproved == true &&
                   user.PlatformTermsAccepted &&
                   (!requireStripePayoutSetup || IsStripePayoutSetupComplete(user));
        }

        private static string ResolveSubscriptionBlockedReason(
            IReadOnlyCollection<string> roles,
            LandlordOnboardingStatusDto? landlordStatus,
            ApplicationUser user,
            bool requireStripePayoutSetup)
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

            if (requireStripePayoutSetup && !IsStripePayoutSetupComplete(user))
            {
                return "Set up your payout account before paying for a subscription.";
            }

            return string.Empty;
        }

        private async Task SendKycSubmittedAdminEmailAsync(ApplicationUser user, LandlordKycProfile profile)
        {
            var recipients = new Dictionary<string, PlatformLanguage>(StringComparer.OrdinalIgnoreCase);

            try
            {
                AddRecipient(_configuration["Notifications:KycAdminEmail"]);
                AddRecipient(_configuration["AdminNotifications:KycEmail"]);
                AddRecipient(_configuration["AdminSeed:Email"]);

                var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
                foreach (var adminUser in adminUsers)
                {
                    AddRecipient(adminUser.Email, adminUser.EmailLanguage);
                }

                if (recipients.Count == 0)
                {
                    _logger.LogWarning(
                        "KYC submitted for user {UserId}, but no admin email recipient is configured.",
                        user.Id);
                    return;
                }

                var reviewUrl = BuildPortalUrl($"/AdminUsers/Overview?userId={Uri.EscapeDataString(user.Id)}");
                var approvalsUrl = BuildPortalUrl($"/AdminUsers/LandlordApprovals?search={Uri.EscapeDataString(user.Email ?? user.Id)}");
                foreach (var recipient in recipients)
                {
                    var isFrench = recipient.Value == PlatformLanguage.French;
                    var subject = isFrench
                        ? $"Approbation KYC requise pour {DisplayNameOrEmail(user)}"
                        : $"KYC approval needed for {DisplayNameOrEmail(user)}";
                    var lines = isFrench
                        ? new[]
                        {
                            "Un bailleur a soumis ses documents KYC et attend une approbation administrative.",
                            string.Empty,
                            $"Nom : {DisplayNameOrEmail(user)}",
                            $"Courriel : {user.Email ?? "Non fourni"}",
                            $"Type de document : {profile.DocumentType}",
                            $"Soumis le : {profile.SubmittedAt:yyyy-MM-dd HH:mm} UTC",
                            string.Empty,
                            "Ouvrir la page d’approbation directe :",
                            reviewUrl,
                            string.Empty,
                            "File des approbations :",
                            approvalsUrl,
                            string.Empty,
                            "Lontsi Homes"
                        }
                        : new[]
                        {
                            "A landlord has submitted KYC documents and needs admin approval.",
                            string.Empty,
                            $"Name: {DisplayNameOrEmail(user)}",
                            $"Email: {user.Email ?? "Not provided"}",
                            $"Document type: {profile.DocumentType}",
                            $"Submitted at: {profile.SubmittedAt:yyyy-MM-dd HH:mm} UTC",
                            string.Empty,
                            "Open the direct approval page:",
                            reviewUrl,
                            string.Empty,
                            "Approvals queue:",
                            approvalsUrl,
                            string.Empty,
                            "Lontsi Homes"
                        };

                    await _emailService.SendEmailAsync(recipient.Key, subject, string.Join(Environment.NewLine, lines));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KYC admin notification failed for user {UserId}.", user.Id);
            }

            void AddRecipient(string? email, PlatformLanguage language = PlatformLanguage.English)
            {
                var value = (email ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    recipients[value] = language;
                }
            }
        }

        private string BuildPortalUrl(string pathAndQuery)
        {
            var configuredBaseUrl =
                _configuration["Portal:BaseUrl"] ??
                _configuration["Portal:Domain"] ??
                _configuration["PORTAL_DOMAIN"] ??
                "https://lontsihomes.com";

            var baseUrl = configuredBaseUrl.Trim().TrimEnd('/');
            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = $"https://{baseUrl}";
            }

            return $"{baseUrl}/{pathAndQuery.TrimStart('/')}";
        }

        private static string DisplayNameOrEmail(ApplicationUser user)
        {
            return !string.IsNullOrWhiteSpace(user.FullName)
                ? user.FullName.Trim()
                : user.Email ?? user.Id;
        }

        private static bool IsStripePayoutSetupComplete(ApplicationUser user)
        {
            return !string.IsNullOrWhiteSpace(user.StripeConnectAccountId) &&
                   user.StripePayoutDetailsSubmitted &&
                   user.StripeChargesEnabled &&
                   user.StripePayoutsEnabled;
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

            AddKycMediaIfPresent(summary, "face-front", "Face - front", profile.FaceFrontPath, profile.FaceFrontOriginalFileName, profile.FaceFrontContentType, mediaBaseUrl);
            AddKycMediaIfPresent(summary, "face-right", "Face - looking right", profile.FaceRightPath, profile.FaceRightOriginalFileName, profile.FaceRightContentType, mediaBaseUrl);
            AddKycMediaIfPresent(summary, "face-left", "Face - looking left", profile.FaceLeftPath, profile.FaceLeftOriginalFileName, profile.FaceLeftContentType, mediaBaseUrl);
            AddKycMediaIfPresent(summary, "document-front", "Document front", profile.DocumentFrontPath, profile.DocumentFrontOriginalFileName, profile.DocumentFrontContentType, mediaBaseUrl);
            if (!string.IsNullOrWhiteSpace(profile.DocumentBackPath))
            {
                AddKycMedia(summary, "document-back", "Document back", profile.DocumentBackOriginalFileName ?? string.Empty, profile.DocumentBackContentType ?? string.Empty, mediaBaseUrl);
            }

            return summary;
        }

        private static void AddKycMediaIfPresent(
            LandlordKycSummaryDto summary,
            string key,
            string label,
            string? path,
            string originalFileName,
            string contentType,
            string? mediaBaseUrl)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                AddKycMedia(summary, key, label, originalFileName, contentType, mediaBaseUrl);
            }
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

        private static string? NormalizeCountryIsoCode(string? countryIsoCode)
        {
            var trimmed = (countryIsoCode ?? string.Empty).Trim().ToUpperInvariant();
            return trimmed.Length == 2 && trimmed.All(char.IsLetter) ? trimmed : null;
        }

        private static string? ResolveCountryIsoFromCountryCode(string? countryCode)
        {
            return NormalizeCountryCode(countryCode) switch
            {
                "+1" => "CA",
                "+237" => "CM",
                "+44" => "GB",
                "+33" => "FR",
                "+32" => "BE",
                "+49" => "DE",
                "+234" => "NG",
                "+225" => "CI",
                "+233" => "GH",
                "+27" => "ZA",
                "+254" => "KE",
                "+971" => "AE",
                _ => null
            };
        }

        private static bool IsCameroonCountry(string? countryIsoCode, string? countryCode)
        {
            return string.Equals(NormalizeCountryIsoCode(countryIsoCode), "CM", StringComparison.OrdinalIgnoreCase) ||
                   IsCameroonCountryCode(countryCode);
        }

        private static bool IsCameroonCountryCode(string? countryCode)
        {
            return string.Equals(NormalizeCountryCode(countryCode), "+237", StringComparison.Ordinal);
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

        private static bool TryNormalizeLocalPhoneNumber(
            string? phoneNumber,
            string countryCode,
            out string normalizedPhoneNumber)
        {
            normalizedPhoneNumber = string.Empty;
            var digits = new string((phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }

            var countryDigits = new string((countryCode ?? string.Empty).Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(countryDigits) &&
                digits.StartsWith(countryDigits, StringComparison.Ordinal))
            {
                digits = digits[countryDigits.Length..];
            }

            if (digits.Length is < 6 or > 15)
            {
                return false;
            }

            normalizedPhoneNumber = digits;
            return true;
        }

        private static void ClearMobileMoneyFields(ApplicationUser user)
        {
            user.UsePrimaryPhoneForSubscriptionPayments = false;
            user.SubscriptionPaymentPhoneNumber = null;
            user.SubscriptionPaymentChannel = null;
            user.IsSubscriptionPaymentPhoneVerified = false;
            user.SubscriptionPaymentPhoneVerifiedAt = null;
            user.UsePrimaryPhoneForRentPayouts = false;
            user.PayoutPhoneNumber = null;
            user.PayoutChannel = null;
            user.IsPayoutPhoneVerified = false;
            user.PayoutPhoneVerifiedAt = null;
            user.WhatsAppPhoneNumber = null;
            user.IsWhatsAppPhoneVerified = false;
            user.WhatsAppPhoneVerifiedAt = null;
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

        private sealed class MobilePaymentRollbackSnapshot
        {
            public string Target { get; set; } = string.Empty;
            public bool UsePrimaryPhone { get; set; }
            public string? PhoneNumber { get; set; }
            public PayoutChannelEnum? Channel { get; set; }
            public bool IsVerified { get; set; }
            public DateTimeOffset? VerifiedAt { get; set; }
        }
    }
}







