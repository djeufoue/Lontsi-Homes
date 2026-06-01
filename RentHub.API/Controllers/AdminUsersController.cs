using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Storage;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class AdminUsersController : ControllerBase
    {
        private const string OtpLoginProvider = "RentHub";
        private static readonly (string Code, string Expiry)[] OtpTokens =
        {
            ("ActivationOtpCode", "ActivationOtpExpiryUnix"),
            ("PhoneOtpCode", "PhoneOtpExpiryUnix"),
            ("SubscriptionPaymentOtpCode", "SubscriptionPaymentOtpExpiryUnix"),
            ("PayoutOtpCode", "PayoutOtpExpiryUnix"),
            ("WhatsAppOtpCode", "WhatsAppOtpExpiryUnix")
        };

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IStorageService _storageService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AdminUsersController> _logger;

        public AdminUsersController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IStorageService storageService,
            IConfiguration configuration,
            ILogger<AdminUsersController> logger)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("verification-status")]
        public async Task<IActionResult> GetVerificationStatus([FromQuery] string? search = null)
        {
            var query = _context.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(u =>
                    (u.Email ?? string.Empty).Contains(term) ||
                    (u.FullName ?? string.Empty).Contains(term) ||
                    (u.PhoneNumber ?? string.Empty).Contains(term));
            }

            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .ThenBy(u => u.Email)
                .Take(100)
                .ToListAsync();

            var results = new List<AdminUserVerificationStatusDto>();
            foreach (var user in users)
            {
                var roles = (await _userManager.GetRolesAsync(user)).ToList();
                results.Add(BuildStatusDto(user, roles));
            }

            return Ok(results);
        }

        [HttpGet("management-permissions")]
        public async Task<IActionResult> GetManagementPermissions()
        {
            return Ok(new AdminUserManagementPermissionsDto
            {
                CanDeleteUsers = await IsCurrentUserMasterAdminAsync()
            });
        }

        [HttpGet("{userId}/overview")]
        public async Task<IActionResult> GetOverview(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound("User was not found.");
            }

            var roles = (await _userManager.GetRolesAsync(user)).ToList();
            var status = BuildStatusDto(user, roles);

            return Ok(new AdminUserOverviewDto
            {
                User = status,
                OtpCodes = await BuildOtpDtosAsync(user),
                Steps = BuildStepDtos(status)
            });
        }

        [HttpGet("{userId}/otp-status")]
        public async Task<IActionResult> GetOtpStatus(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound("User was not found.");
            }

            return Ok(await BuildOtpDtosAsync(user));
        }

        [HttpPost("{userId}/restart-validation")]
        public async Task<IActionResult> RestartValidation(string userId)
        {
            try
            {
                var currentUserId = UserHelpers.GetUserId(User);
                if (string.Equals(currentUserId, userId, StringComparison.Ordinal))
                {
                    return BadRequest("You cannot restart validation for your own admin session.");
                }

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    return NotFound("User was not found.");
                }

                if (await _userManager.IsInRoleAsync(user, "Admin"))
                {
                    return BadRequest("Admin accounts cannot be reset from this test tool.");
                }

                user.EmailConfirmed = false;
                user.CountryCode = null;
                user.PhoneNumber = null;
                user.PhoneNumberConfirmed = false;
                user.UsePrimaryPhoneForSubscriptionPayments = true;
                user.SubscriptionPaymentPhoneNumber = null;
                user.SubscriptionPaymentChannel = null;
                user.IsSubscriptionPaymentPhoneVerified = false;
                user.SubscriptionPaymentPhoneVerifiedAt = null;
                user.UsePrimaryPhoneForRentPayouts = true;
                user.PayoutPhoneNumber = null;
                user.PayoutChannel = null;
                user.IsPayoutPhoneVerified = false;
                user.PayoutPhoneVerifiedAt = null;
                user.WhatsAppPhoneNumber = null;
                user.IsWhatsAppPhoneVerified = false;
                user.WhatsAppPhoneVerifiedAt = null;

                await _userManager.UpdateSecurityStampAsync(user);

                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(updateResult.Errors);
                }

                foreach (var (code, expiry) in OtpTokens)
                {
                    await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, code);
                    await _userManager.RemoveAuthenticationTokenAsync(user, OtpLoginProvider, expiry);
                }

                return Ok(new { Message = "Validation state restarted. Email and name were kept." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restart validation for user {UserId}", userId);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "ADMIN_USER_VALIDATION_RESET_FAILED",
                    Message = "Unable to restart this user's validation right now."
                });
            }
        }

        [HttpDelete("{userId}")]
        public async Task<IActionResult> DeleteUser(string userId)
        {
            if (!await IsCurrentUserMasterAdminAsync())
            {
                return Forbid();
            }

            var currentUserId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Forbid();
            }

            if (string.Equals(currentUserId, userId, StringComparison.Ordinal))
            {
                return BadRequest("You cannot delete your own admin account.");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound("User was not found.");
            }

            if (await _userManager.IsInRoleAsync(user, "Admin"))
            {
                return BadRequest("Admin accounts cannot be deleted from this test tool.");
            }

            var deletedUserLabel = user.Email ?? user.UserName ?? user.Id;

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var documentBlobUrls = await DeleteUserRelatedDataAsync(userId, currentUserId);

                var deleteResult = await _userManager.DeleteAsync(user);
                if (!deleteResult.Succeeded)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(deleteResult.Errors);
                }

                await transaction.CommitAsync();
                await DeleteDocumentFilesAsync(documentBlobUrls, userId);

                _logger.LogWarning(
                    "Master admin {AdminUserId} deleted user {DeletedUserLabel} ({DeletedUserId}) and related records.",
                    currentUserId,
                    deletedUserLabel,
                    userId);

                return Ok(new { Message = "User and related records were deleted." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to delete user {UserId}", userId);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "ADMIN_USER_DELETE_FAILED",
                    Message = "Unable to delete this user right now."
                });
            }
        }

        private static AdminUserVerificationStatusDto BuildStatusDto(ApplicationUser user, List<string> roles)
        {
            var nextStep = ResolveLandlordOnboardingStep(user, roles);
            return new AdminUserVerificationStatusDto
            {
                UserId = user.Id,
                Email = user.Email ?? string.Empty,
                FullName = user.FullName ?? string.Empty,
                CountryCode = user.CountryCode,
                PhoneNumber = user.PhoneNumber,
                EmailConfirmed = user.EmailConfirmed,
                PhoneNumberConfirmed = user.PhoneNumberConfirmed,
                SubscriptionPaymentPhoneNumber = user.SubscriptionPaymentPhoneNumber,
                SubscriptionPaymentChannel = user.SubscriptionPaymentChannel,
                IsSubscriptionPaymentPhoneVerified = user.IsSubscriptionPaymentPhoneVerified,
                SubscriptionPaymentPhoneVerifiedAt = user.SubscriptionPaymentPhoneVerifiedAt,
                PayoutPhoneNumber = user.PayoutPhoneNumber,
                PayoutChannel = user.PayoutChannel,
                IsPayoutPhoneVerified = user.IsPayoutPhoneVerified,
                PayoutPhoneVerifiedAt = user.PayoutPhoneVerifiedAt,
                WhatsAppPhoneNumber = user.WhatsAppPhoneNumber,
                IsWhatsAppPhoneVerified = user.IsWhatsAppPhoneVerified,
                WhatsAppPhoneVerifiedAt = user.WhatsAppPhoneVerifiedAt,
                CreatedAt = user.CreatedAt,
                Roles = roles,
                NextOnboardingStep = nextStep,
                IsOnboardingComplete = nextStep == LandlordOnboardingSteps.Complete
            };
        }

        private async Task<bool> IsCurrentUserMasterAdminAsync()
        {
            var currentUserId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return false;
            }

            var currentUser = await _userManager.FindByIdAsync(currentUserId);
            if (currentUser == null || !await _userManager.IsInRoleAsync(currentUser, "Admin"))
            {
                return false;
            }

            var masterAdminEmail = _configuration["AdminSeed:Email"];
            return !string.IsNullOrWhiteSpace(masterAdminEmail) &&
                   string.Equals(currentUser.Email, masterAdminEmail, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<List<string>> DeleteUserRelatedDataAsync(string userId, string replacementUserId)
        {
            var ownedPropertyIds = await _context.Properties
                .IgnoreQueryFilters()
                .Where(p => p.LandlordId == userId)
                .Select(p => p.Id)
                .ToListAsync();

            var ownedApartmentIds = await _context.Apartments
                .IgnoreQueryFilters()
                .Where(a => ownedPropertyIds.Contains(a.PropertyId))
                .Select(a => a.Id)
                .ToListAsync();

            var ownedTenancyIds = await _context.Tenancies
                .IgnoreQueryFilters()
                .Where(t => ownedApartmentIds.Contains(t.ApartmentId))
                .Select(t => t.Id)
                .ToListAsync();

            var conversationIds = await _context.ApartmentConversations
                .Where(c =>
                    c.LandlordId == userId ||
                    c.VisitorId == userId ||
                    ownedApartmentIds.Contains(c.ApartmentId))
                .Select(c => c.Id)
                .ToListAsync();

            var documentBlobUrls = await _context.Documents
                .IgnoreQueryFilters()
                .Where(d =>
                    d.UserId == userId ||
                    (d.PropertyId.HasValue && ownedPropertyIds.Contains(d.PropertyId.Value)) ||
                    (d.ApartmentId.HasValue && ownedApartmentIds.Contains(d.ApartmentId.Value)) ||
                    (d.TenancyId.HasValue && ownedTenancyIds.Contains(d.TenancyId.Value)))
                .Select(d => d.BlobUrl)
                .Where(blobUrl => !string.IsNullOrWhiteSpace(blobUrl))
                .Distinct()
                .ToListAsync();

            var userPaymentReferences = await _context.Payments
                .IgnoreQueryFilters()
                .Where(p =>
                    p.TenantId == userId ||
                    p.LandlordId == userId ||
                    (p.TenancyId.HasValue && ownedTenancyIds.Contains(p.TenancyId.Value)))
                .Select(p => new { p.RequestKey, p.TransactionId })
                .ToListAsync();

            var subscriptionPaymentReferences = await _context.UserSubscriptions
                .IgnoreQueryFilters()
                .Where(us => us.UserId == userId)
                .Select(us => new { us.PaymentReference, us.PaymentProviderTransactionId })
                .ToListAsync();

            var webhookPaymentReferences = userPaymentReferences
                .Select(p => p.RequestKey)
                .Concat(subscriptionPaymentReferences.Select(s => s.PaymentReference))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct()
                .ToList();

            var webhookTransactionIds = userPaymentReferences
                .Select(p => p.TransactionId)
                .Concat(subscriptionPaymentReferences.Select(s => s.PaymentProviderTransactionId))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct()
                .ToList();

            if (webhookPaymentReferences.Count > 0 || webhookTransactionIds.Count > 0)
            {
                await _context.PaymentWebhookEvents
                    .Where(e =>
                        (e.PaymentReference != null && webhookPaymentReferences.Contains(e.PaymentReference)) ||
                        (e.ProviderTransactionId != null && webhookTransactionIds.Contains(e.ProviderTransactionId)))
                    .ExecuteDeleteAsync();
            }

            await _context.TenancyExtensionRequests
                .IgnoreQueryFilters()
                .Where(r => r.ApprovedById == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.ApprovedById, (string?)null));

            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [Documents] SET [DeletedByUserId] = NULL WHERE [DeletedByUserId] = {userId}");

            var now = DateTimeOffset.UtcNow;
            await _context.Apartments
                .IgnoreQueryFilters()
                .Where(a => a.AdderId == userId && !ownedPropertyIds.Contains(a.PropertyId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.AdderId, replacementUserId)
                    .SetProperty(a => a.UpdatedBy, replacementUserId)
                    .SetProperty(a => a.UpdatedAt, now));

            await _context.ConversationMessages
                .Where(m => m.SenderId == userId || conversationIds.Contains(m.ConversationId))
                .ExecuteDeleteAsync();

            await _context.ApartmentConversations
                .Where(c =>
                    c.LandlordId == userId ||
                    c.VisitorId == userId ||
                    ownedApartmentIds.Contains(c.ApartmentId))
                .ExecuteDeleteAsync();

            await _context.TenancyExtensionRequests
                .IgnoreQueryFilters()
                .Where(r => r.RequestedById == userId || ownedTenancyIds.Contains(r.TenancyId))
                .ExecuteDeleteAsync();

            await _context.Payments
                .IgnoreQueryFilters()
                .Where(p =>
                    p.TenantId == userId ||
                    p.LandlordId == userId ||
                    (p.TenancyId.HasValue && ownedTenancyIds.Contains(p.TenancyId.Value)))
                .ExecuteDeleteAsync();

            await _context.Documents
                .IgnoreQueryFilters()
                .Where(d =>
                    d.UserId == userId ||
                    (d.PropertyId.HasValue && ownedPropertyIds.Contains(d.PropertyId.Value)) ||
                    (d.ApartmentId.HasValue && ownedApartmentIds.Contains(d.ApartmentId.Value)) ||
                    (d.TenancyId.HasValue && ownedTenancyIds.Contains(d.TenancyId.Value)))
                .ExecuteDeleteAsync();

            await _context.TenancyMembers
                .IgnoreQueryFilters()
                .Where(tm => tm.MemberId == userId || ownedTenancyIds.Contains(tm.TenancyId))
                .ExecuteDeleteAsync();

            await _context.Tenancies
                .IgnoreQueryFilters()
                .Where(t => ownedTenancyIds.Contains(t.Id))
                .ExecuteDeleteAsync();

            await _context.ApartmentOwners
                .IgnoreQueryFilters()
                .Where(o => o.OwnerId == userId || ownedApartmentIds.Contains(o.ApartmentId))
                .ExecuteDeleteAsync();

            await _context.PropertyManagerAssignments
                .IgnoreQueryFilters()
                .Where(m => m.ManagerId == userId || ownedPropertyIds.Contains(m.PropertyId))
                .ExecuteDeleteAsync();

            await _context.ReminderSettings
                .IgnoreQueryFilters()
                .Where(rs => rs.LandlordId == userId || (rs.PropertyId.HasValue && ownedPropertyIds.Contains(rs.PropertyId.Value)))
                .ExecuteDeleteAsync();

            await _context.UserSubscriptions
                .IgnoreQueryFilters()
                .Where(us => us.UserId == userId)
                .ExecuteDeleteAsync();

            await _context.Apartments
                .IgnoreQueryFilters()
                .Where(a => ownedApartmentIds.Contains(a.Id))
                .ExecuteDeleteAsync();

            await _context.Properties
                .IgnoreQueryFilters()
                .Where(p => ownedPropertyIds.Contains(p.Id))
                .ExecuteDeleteAsync();

            return documentBlobUrls;
        }

        private async Task DeleteDocumentFilesAsync(IEnumerable<string> blobUrls, string deletedUserId)
        {
            foreach (var blobUrl in blobUrls.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
            {
                try
                {
                    await _storageService.DeleteFileAsync(blobUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Deleted user {DeletedUserId}, but failed to delete document file {BlobUrl}.",
                        deletedUserId,
                        blobUrl);
                }
            }
        }

        private async Task<List<AdminUserOtpDto>> BuildOtpDtosAsync(ApplicationUser user)
        {
            var result = new List<AdminUserOtpDto>();
            foreach (var (codeToken, expiryToken) in OtpTokens)
            {
                var code = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, codeToken);
                var expiryValue = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, expiryToken);
                var expiresAt = ParseUnixExpiry(expiryValue);
                var isUsed = IsOtpUsed(user, codeToken);
                var isRequired = IsOtpRequired(user, codeToken);
                var remainingSeconds = expiresAt.HasValue
                    ? Math.Max(0, (int)Math.Floor((expiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds))
                    : (int?)null;
                var isExpired = !isUsed && expiresAt.HasValue && expiresAt.Value <= DateTimeOffset.UtcNow;
                var hasCode = !string.IsNullOrWhiteSpace(code);
                var (status, statusLabel) = ResolveOtpStatus(isRequired, isUsed, hasCode, isExpired);

                result.Add(new AdminUserOtpDto
                {
                    Key = codeToken,
                    Label = OtpLabel(codeToken),
                    Channel = OtpChannel(codeToken),
                    Destination = OtpDestination(user, codeToken),
                    Code = code,
                    ExpiresAt = expiresAt,
                    UsedAt = OtpUsedAt(user, codeToken),
                    HasCode = hasCode,
                    IsUsed = isUsed,
                    IsExpired = isExpired,
                    IsRequired = isRequired,
                    RemainingSeconds = remainingSeconds,
                    Status = status,
                    StatusLabel = statusLabel
                });
            }

            return result;
        }

        private static List<AdminUserOnboardingStepDto> BuildStepDtos(AdminUserVerificationStatusDto status)
        {
            var isAdmin = status.Roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
            if (isAdmin)
            {
                return new List<AdminUserOnboardingStepDto>
                {
                    CreateStep("admin-account", "Admin account", "The admin account exists and can access the system.", true, false, status.Email),
                    CreateStep("admin-bypass", "Validation bypassed", "System administrators are excluded from landlord verification.", true, true, "No OTP validation required.")
                };
            }

            var isLandlord = status.Roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase));
            if (!isLandlord)
            {
                return new List<AdminUserOnboardingStepDto>
                {
                    CreateStep(LandlordOnboardingSteps.Account, "Account created", "The user record exists in the system.", !string.IsNullOrWhiteSpace(status.Email), status.NextOnboardingStep == LandlordOnboardingSteps.Account, status.Email),
                    CreateStep(LandlordOnboardingSteps.Email, "Email verified", "The user confirms the email OTP.", status.EmailConfirmed, status.NextOnboardingStep == LandlordOnboardingSteps.Email, status.EmailConfirmed ? "Email confirmed." : "Waiting for email OTP."),
                    CreateStep(LandlordOnboardingSteps.Complete, "Ready", "Non-landlord account validation state.", status.IsOnboardingComplete, status.NextOnboardingStep == LandlordOnboardingSteps.Complete, status.IsOnboardingComplete ? "Account can continue." : "Account is still pending.")
                };
            }

            var hasSubscriptionPaymentDetails =
                !string.IsNullOrWhiteSpace(status.SubscriptionPaymentPhoneNumber) &&
                status.SubscriptionPaymentChannel is PayoutChannelEnum.MtnMoney or PayoutChannelEnum.OrangeMoney;

            var hasPayoutDetails =
                !string.IsNullOrWhiteSpace(status.PayoutPhoneNumber) &&
                status.PayoutChannel is PayoutChannelEnum.MtnMoney or PayoutChannelEnum.OrangeMoney;

            var whatsAppReady = string.IsNullOrWhiteSpace(status.WhatsAppPhoneNumber) || status.IsWhatsAppPhoneVerified;
            var mobilePaymentVerificationComplete =
                status.IsSubscriptionPaymentPhoneVerified &&
                status.IsPayoutPhoneVerified &&
                whatsAppReady;

            return new List<AdminUserOnboardingStepDto>
            {
                CreateStep(LandlordOnboardingSteps.Account, "Account created", "Name, email, and landlord role are registered.", !string.IsNullOrWhiteSpace(status.Email), status.NextOnboardingStep == LandlordOnboardingSteps.Account, status.Email),
                CreateStep(LandlordOnboardingSteps.Email, "Email verified", "The landlord confirms the email OTP.", status.EmailConfirmed, status.NextOnboardingStep == LandlordOnboardingSteps.Email, status.EmailConfirmed ? "Email confirmed." : "Waiting for email OTP."),
                CreateStep(LandlordOnboardingSteps.Phone, "Primary phone verified", "The landlord confirms the SMS OTP for the primary phone.", !string.IsNullOrWhiteSpace(status.PhoneNumber) && status.PhoneNumberConfirmed, status.NextOnboardingStep == LandlordOnboardingSteps.Phone, status.PhoneNumberConfirmed ? status.PhoneNumber : "Waiting for primary phone OTP."),
                CreateStep(LandlordOnboardingSteps.MobilePayments, "Mobile money configured", "Subscription and rent payout numbers are selected with MTN or Orange Money.", hasSubscriptionPaymentDetails && hasPayoutDetails, status.NextOnboardingStep == LandlordOnboardingSteps.MobilePayments, MobileMoneyDetails(status)),
                CreateStep(LandlordOnboardingSteps.MobilePaymentVerification, "Payment numbers verified", "Every distinct mobile transaction number is validated by OTP.", mobilePaymentVerificationComplete, status.NextOnboardingStep == LandlordOnboardingSteps.MobilePaymentVerification, MobileVerificationDetails(status)),
                CreateStep(LandlordOnboardingSteps.Complete, "Registration ready", "The landlord can continue into the authenticated workspace.", status.IsOnboardingComplete, status.NextOnboardingStep == LandlordOnboardingSteps.Complete, status.IsOnboardingComplete ? "All required steps are complete." : "Some verification work remains.")
            };
        }

        private static AdminUserOnboardingStepDto CreateStep(
            string key,
            string title,
            string description,
            bool isComplete,
            bool isCurrent,
            string? detail)
        {
            return new AdminUserOnboardingStepDto
            {
                Key = key,
                Title = title,
                Description = description,
                Detail = detail,
                IsComplete = isComplete,
                IsCurrent = isCurrent
            };
        }

        private static DateTimeOffset? ParseUnixExpiry(string? value)
        {
            if (long.TryParse(value, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            return null;
        }

        private static bool IsOtpUsed(ApplicationUser user, string codeToken)
        {
            return codeToken switch
            {
                "ActivationOtpCode" => user.EmailConfirmed,
                "PhoneOtpCode" => user.PhoneNumberConfirmed,
                "SubscriptionPaymentOtpCode" => user.IsSubscriptionPaymentPhoneVerified,
                "PayoutOtpCode" => user.IsPayoutPhoneVerified,
                "WhatsAppOtpCode" => string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) || user.IsWhatsAppPhoneVerified,
                _ => false
            };
        }

        private static bool IsOtpRequired(ApplicationUser user, string codeToken)
        {
            return codeToken switch
            {
                "ActivationOtpCode" => !string.IsNullOrWhiteSpace(user.Email),
                "PhoneOtpCode" => !string.IsNullOrWhiteSpace(user.PhoneNumber),
                "SubscriptionPaymentOtpCode" => !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber),
                "PayoutOtpCode" => !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber),
                "WhatsAppOtpCode" => !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber),
                _ => false
            };
        }

        private static DateTimeOffset? OtpUsedAt(ApplicationUser user, string codeToken)
        {
            return codeToken switch
            {
                "SubscriptionPaymentOtpCode" => user.SubscriptionPaymentPhoneVerifiedAt,
                "PayoutOtpCode" => user.PayoutPhoneVerifiedAt,
                "WhatsAppOtpCode" => user.WhatsAppPhoneVerifiedAt,
                _ => null
            };
        }

        private static (string Status, string StatusLabel) ResolveOtpStatus(
            bool isRequired,
            bool isUsed,
            bool hasCode,
            bool isExpired)
        {
            if (!isRequired)
            {
                return ("not-required", "Not required");
            }

            if (isUsed)
            {
                return ("used", "Used");
            }

            if (isExpired)
            {
                return ("expired", "Expired");
            }

            if (hasCode)
            {
                return ("active", "Active");
            }

            return ("not-generated", "Not generated");
        }

        private static string OtpLabel(string codeToken)
        {
            return codeToken switch
            {
                "ActivationOtpCode" => "Email verification OTP",
                "PhoneOtpCode" => "Primary phone OTP",
                "SubscriptionPaymentOtpCode" => "Subscription payment OTP",
                "PayoutOtpCode" => "Rent payout OTP",
                "WhatsAppOtpCode" => "WhatsApp OTP",
                _ => "Verification OTP"
            };
        }

        private static string OtpChannel(string codeToken)
        {
            return codeToken switch
            {
                "ActivationOtpCode" => "Email",
                "WhatsAppOtpCode" => "WhatsApp",
                _ => "SMS"
            };
        }

        private static string? OtpDestination(ApplicationUser user, string codeToken)
        {
            return codeToken switch
            {
                "ActivationOtpCode" => user.Email,
                "PhoneOtpCode" => user.PhoneNumber,
                "SubscriptionPaymentOtpCode" => user.SubscriptionPaymentPhoneNumber,
                "PayoutOtpCode" => user.PayoutPhoneNumber,
                "WhatsAppOtpCode" => user.WhatsAppPhoneNumber,
                _ => null
            };
        }

        private static string MobileMoneyDetails(AdminUserVerificationStatusDto status)
        {
            var subscription = string.IsNullOrWhiteSpace(status.SubscriptionPaymentPhoneNumber)
                ? "Subscription: not configured"
                : $"Subscription: {status.SubscriptionPaymentPhoneNumber} via {ChannelLabel(status.SubscriptionPaymentChannel)}";

            var payout = string.IsNullOrWhiteSpace(status.PayoutPhoneNumber)
                ? "Payout: not configured"
                : $"Payout: {status.PayoutPhoneNumber} via {ChannelLabel(status.PayoutChannel)}";

            return $"{subscription}. {payout}.";
        }

        private static string MobileVerificationDetails(AdminUserVerificationStatusDto status)
        {
            var whatsApp = string.IsNullOrWhiteSpace(status.WhatsAppPhoneNumber)
                ? "WhatsApp: not requested"
                : $"WhatsApp: {(status.IsWhatsAppPhoneVerified ? "verified" : "pending")} ({status.WhatsAppPhoneNumber})";

            return $"Subscription: {(status.IsSubscriptionPaymentPhoneVerified ? "verified" : "pending")}. Payout: {(status.IsPayoutPhoneVerified ? "verified" : "pending")}. {whatsApp}.";
        }

        private static string ChannelLabel(PayoutChannelEnum? channel)
        {
            return channel switch
            {
                PayoutChannelEnum.MtnMoney => "MTN Money",
                PayoutChannelEnum.OrangeMoney => "Orange Money",
                _ => "not configured"
            };
        }

        private static string ResolveLandlordOnboardingStep(ApplicationUser user, IReadOnlyCollection<string> roles)
        {
            if (roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase)))
            {
                return LandlordOnboardingSteps.Complete;
            }

            if (!roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase)))
            {
                return user.EmailConfirmed ? LandlordOnboardingSteps.Complete : LandlordOnboardingSteps.Email;
            }

            if (!user.EmailConfirmed)
            {
                return LandlordOnboardingSteps.Email;
            }

            if (string.IsNullOrWhiteSpace(user.PhoneNumber) || !user.PhoneNumberConfirmed)
            {
                return LandlordOnboardingSteps.Phone;
            }

            var hasSubscriptionPaymentDetails =
                !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber) &&
                user.SubscriptionPaymentChannel is PayoutChannelEnum.MtnMoney or PayoutChannelEnum.OrangeMoney;

            var hasPayoutDetails =
                !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber) &&
                user.PayoutChannel is PayoutChannelEnum.MtnMoney or PayoutChannelEnum.OrangeMoney;

            if (!hasSubscriptionPaymentDetails || !hasPayoutDetails)
            {
                return LandlordOnboardingSteps.MobilePayments;
            }

            var whatsAppReady = string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) || user.IsWhatsAppPhoneVerified;
            if (!user.IsSubscriptionPaymentPhoneVerified || !user.IsPayoutPhoneVerified || !whatsAppReady)
            {
                return LandlordOnboardingSteps.MobilePaymentVerification;
            }

            return LandlordOnboardingSteps.Complete;
        }
    }
}
