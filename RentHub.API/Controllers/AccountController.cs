using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Auth;
using RentHub.API.Services.Email;
using System.Security.Claims;
using System.Security.Cryptography;
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

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly TokenService _tokenService;
        private readonly IEmailService _emailService;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager,
            ApplicationDbContext context,
            TokenService tokenService,
            IEmailService emailService,
            ILogger<AccountController> logger)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _tokenService = tokenService;
            _emailService = emailService;
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

                if (!request.PlanId.HasValue)
                {
                    return BadRequest("Please choose a subscription plan to complete landlord registration.");
                }

                var existingUser = await _userManager.FindByEmailAsync(request.Email);
                if (existingUser != null)
                {
                    return BadRequest("An account with this email already exists. Please log in instead.");
                }

                var plan = await _context.SubscriptionPlans.FindAsync(request.PlanId.Value);
                if (plan == null)
                {
                    return BadRequest("Invalid subscription plan.");
                }

                var user = new ApplicationUser
                {
                    UserName = request.Email,
                    Email = request.Email,
                    FullName = string.Join(" ", new[] { request.FirstName?.Trim(), request.LastName?.Trim() }.Where(v => !string.IsNullOrWhiteSpace(v))),
                    CountryCode = request.CountryCode?.Trim(),
                    PhoneNumber = request.PhoneNumber?.Trim(),
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
                    IsApproved = false,
                    CreatedBy = user.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.UserSubscriptions.Add(subscription);
                await _context.SaveChangesAsync();

                await IssueActivationOtpAsync(user);

                return Ok(new
                {
                    RequiresActivation = true,
                    Email = user.Email,
                    Message = "Registration successful. Check your email for an OTP code to activate your account."
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

                var storedOtp = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, ActivationOtpTokenName);
                var storedExpiry = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, ActivationOtpExpiryTokenName);

                if (string.IsNullOrWhiteSpace(storedOtp) || string.IsNullOrWhiteSpace(storedExpiry))
                {
                    return BadRequest("No valid OTP found. Please request a new code.");
                }

                if (!long.TryParse(storedExpiry, out var expiryUnix))
                {
                    return BadRequest("Invalid OTP state. Please request a new code.");
                }

                var expiryUtc = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
                if (expiryUtc <= DateTimeOffset.UtcNow)
                {
                    return BadRequest("OTP has expired. Please request a new code.");
                }

                if (!string.Equals(storedOtp, request.Otp.Trim(), StringComparison.Ordinal))
                {
                    return BadRequest("Invalid OTP.");
                }

                user.EmailConfirmed = true;
                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, ActivationOtpTokenName);
                await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, ActivationOtpExpiryTokenName);

                var token = await _tokenService.GenerateTokenAsync(user);
                return Ok(new { Message = "Account activated successfully.", Token = token });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "VerifyActivationOtp", "Unable to verify OTP right now. Please try again.");
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
                    return BadRequest("Account is already activated.");
                }

                await IssueActivationOtpAsync(user);
                return Ok(new { Message = "A new OTP has been sent to your email." });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "ResendActivationOtp", "Unable to resend OTP right now. Please try again.");
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

                if (!user.EmailConfirmed)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "EMAIL_NOT_CONFIRMED",
                        Email = user.Email,
                        Message = "Account is not activated. Verify OTP to complete account activation."
                    });
                }

                var token = await _tokenService.GenerateTokenAsync(user);
                return Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return ServerError(ex, "Login", "Unable to sign in right now. Please try again.");
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
                    .Where(t => t.TenantId == userId && (t.EndDate == null || t.EndDate > now))
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

        private async Task IssueActivationOtpAsync(ApplicationUser user)
        {
            var otp = GenerateOtpCode();
            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, ActivationOtpTokenName, otp);
            await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, ActivationOtpExpiryTokenName, expiry.ToUnixTimeSeconds().ToString());

            var subject = "RentHub Account Activation OTP";
            var body = $@"Hello {(string.IsNullOrWhiteSpace(user.FullName) ? "User" : user.FullName)},

Your RentHub activation OTP is: {otp}
This code expires in 10 minutes.

If you did not create this account, please ignore this email.";

            await _emailService.SendEmailAsync(user.Email ?? string.Empty, subject, body);
        }

        private static string GenerateOtpCode()
        {
            var value = RandomNumberGenerator.GetInt32(0, 1000000);
            return value.ToString("D6");
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
    }
}






