using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Auth;
using RentHub.API.Services.Email;
using RentHub.API.Services.Users;
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
        private const string PayoutOtpTokenName = "PayoutOtpCode";
        private const string PayoutOtpExpiryTokenName = "PayoutOtpExpiryUnix";
        private const string WhatsAppOtpTokenName = "WhatsAppOtpCode";
        private const string WhatsAppOtpExpiryTokenName = "WhatsAppOtpExpiryUnix";

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly TokenService _tokenService;
        private readonly IEmailService _emailService;
        private readonly IUserOnboardingService _userOnboardingService;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager,
            ApplicationDbContext context,
            TokenService tokenService,
            IEmailService emailService,
            IUserOnboardingService userOnboardingService,
            ILogger<AccountController> logger)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _tokenService = tokenService;
            _emailService = emailService;
            _userOnboardingService = userOnboardingService;
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

                var user = new ApplicationUser
                {
                    UserName = request.Email,
                    Email = request.Email,
                    FullName = string.Join(" ", new[] { request.FirstName?.Trim(), request.LastName?.Trim() }.Where(v => !string.IsNullOrWhiteSpace(v))),
                    CountryCode = request.CountryCode?.Trim(),
                    PhoneNumber = request.PhoneNumber?.Trim(),
                    PayoutPhoneNumber = request.PayoutPhoneNumber.Trim(),
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

                var roles = await _userManager.GetRolesAsync(user);
                var isVisitor = roles.Any(r => string.Equals(r, "Visitor", StringComparison.OrdinalIgnoreCase));

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
                else
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

                var roles = await _userManager.GetRolesAsync(user);
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
                    .Where(us => us.UserId == userId && !us.IsDeleted && us.EndDate > now)
                    .OrderByDescending(us => us.EndDate)
                    .ThenByDescending(us => us.StartDate)
                    .FirstOrDefaultAsync();

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
                    PayoutPhoneNumber = user.PayoutPhoneNumber,
                    PayoutChannel = user.PayoutChannel,
                    IsPayoutPhoneVerified = user.IsPayoutPhoneVerified,
                    WhatsAppPhoneNumber = user.WhatsAppPhoneNumber,
                    IsWhatsAppPhoneVerified = user.IsWhatsAppPhoneVerified,
                    Roles = roles.ToList(),
                    PropertyCount = properties.Count,
                    ApartmentCount = properties.Sum(p => p.ApartmentCount),
                    HasActiveSubscription = activeSubscription != null,
                    SubscriptionApproved = activeSubscription?.IsApproved ?? false,
                    CurrentPlanId = activeSubscription?.SubscriptionPlanId,
                    CurrentPlanName = activeSubscription?.PlanNameSnapshot ?? string.Empty,
                    CurrentPlanPrice = activeSubscription?.PlanPriceSnapshot,
                    CurrentPlanDurationInDays = activeSubscription?.PlanDurationInDaysSnapshot,
                    SubscriptionStartDate = activeSubscription?.StartDate,
                    SubscriptionEndDate = activeSubscription?.EndDate,
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







