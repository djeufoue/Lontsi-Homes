using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Helpers;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Services.Email;
using LontsiHomes.API.Services.Kyc;
using LontsiHomes.API.Services.Storage;

namespace LontsiHomes.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AdminUsersController : ControllerBase
    {
        private const string OtpLoginProvider = "RentHub"; // Persisted Identity token provider; retain for existing accounts.
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
        private readonly IKycFileStorageService _kycFileStorageService;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private bool RequireMainPhoneVerification => _configuration.GetValue("Onboarding:RequireMainPhoneVerification", true);
        private readonly ILogger<AdminUsersController> _logger;

        public AdminUsersController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IStorageService storageService,
            IKycFileStorageService kycFileStorageService,
            IEmailService emailService,
            IConfiguration configuration,
            ILogger<AdminUsersController> logger)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
            _kycFileStorageService = kycFileStorageService;
            _emailService = emailService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("verification-status")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetVerificationStatus([FromQuery] string? search = null)
        {
            var query = _context.Users.AsNoTracking();

            var landlordRoleId = await _context.Roles
                .Where(r => r.NormalizedName == "LANDLORD")
                .Select(r => r.Id)
                .FirstOrDefaultAsync();

            if (!string.IsNullOrWhiteSpace(landlordRoleId))
            {
                var landlordUserIds = _context.UserRoles
                    .Where(ur => ur.RoleId == landlordRoleId)
                    .Select(ur => ur.UserId);

                query = query.Where(u => !landlordUserIds.Contains(u.Id));
            }

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
                var kyc = roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase))
                    ? await _context.LandlordKycProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == user.Id)
                    : null;
                results.Add(BuildStatusDto(user, roles, kyc));
            }

            return Ok(results);
        }

        [HttpGet("landlord-verification-status")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetLandlordVerificationStatus([FromQuery] string? search = null)
        {
            var landlordRoleId = await _context.Roles
                .Where(r => r.NormalizedName == "LANDLORD")
                .Select(r => r.Id)
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(landlordRoleId))
            {
                return Ok(new List<AdminUserVerificationStatusDto>());
            }

            var landlordUserIds = _context.UserRoles
                .Where(ur => ur.RoleId == landlordRoleId)
                .Select(ur => ur.UserId);

            var query = _context.Users
                .AsNoTracking()
                .Where(u => landlordUserIds.Contains(u.Id));

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
                var kyc = await _context.LandlordKycProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.UserId == user.Id);
                results.Add(BuildStatusDto(user, roles, kyc));
            }

            return Ok(results);
        }

        [HttpGet("landlord-approvals")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetLandlordApprovals([FromQuery] string? search = null)
        {
            var landlordRoleId = await _context.Roles
                .Where(r => r.NormalizedName == "LANDLORD")
                .Select(r => r.Id)
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(landlordRoleId))
            {
                return Ok(new List<AdminLandlordApprovalDto>());
            }

            var landlordUserIds = _context.UserRoles
                .Where(ur => ur.RoleId == landlordRoleId)
                .Select(ur => ur.UserId);

            var query =
                from user in _context.Users.AsNoTracking()
                join kyc in _context.LandlordKycProfiles.AsNoTracking()
                    on user.Id equals kyc.UserId into kycJoin
                from kyc in kycJoin.DefaultIfEmpty()
                where landlordUserIds.Contains(user.Id)
                select new { user, kyc };

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(item =>
                    (item.user.Email ?? string.Empty).Contains(term) ||
                    (item.user.FullName ?? string.Empty).Contains(term) ||
                    (item.user.PhoneNumber ?? string.Empty).Contains(term));
            }

            var rows = await query
                .OrderBy(item => item.kyc == null
                    ? 1
                    : item.kyc.Status == LandlordKycStatusEnum.Submitted
                        ? 0
                        : item.kyc.Status == LandlordKycStatusEnum.Rejected
                            ? 2
                            : 3)
                .ThenBy(item => item.kyc == null ? item.user.CreatedAt : item.kyc.SubmittedAt)
                .Take(100)
                .ToListAsync();

            return Ok(rows.Select(item => new AdminLandlordApprovalDto
            {
                UserId = item.user.Id,
                Email = item.user.Email ?? string.Empty,
                FullName = item.user.FullName ?? string.Empty,
                CreatedAt = item.user.CreatedAt,
                DocumentType = item.kyc?.DocumentType,
                KycStatus = item.kyc?.Status ?? LandlordKycStatusEnum.NotStarted,
                KycSubmittedAt = item.kyc?.SubmittedAt,
                PlatformTermsAccepted = item.user.PlatformTermsAccepted,
                PlatformTermsAcceptedAt = item.user.PlatformTermsAcceptedAt
            }));
        }

        [HttpGet("management-permissions")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetManagementPermissions()
        {
            return Ok(new AdminUserManagementPermissionsDto
            {
                CanDeleteUsers = await IsCurrentUserMasterAdminAsync()
            });
        }

        [HttpPut("{userId}/subscription-exemption")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateSubscriptionExemption(
            string userId,
            UpdateLandlordSubscriptionExemptionRequest request)
        {
            var landlord = await _userManager.FindByIdAsync(userId);
            if (landlord == null)
            {
                return NotFound(new { Message = "Landlord account was not found." });
            }

            if (!await _userManager.IsInRoleAsync(landlord, "Landlord"))
            {
                return BadRequest(new { Message = "Subscription exemptions can only be assigned to landlords." });
            }

            var cancelledSubscriptionCount = 0;
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                if (request.Enabled)
                {
                    var now = DateTimeOffset.UtcNow;
                    var currentAdminId = UserHelpers.GetUserId(User);
                    var subscriptions = await _context.UserSubscriptions
                        .Where(subscription =>
                            subscription.UserId == userId &&
                            !subscription.IsDeleted &&
                            (subscription.EndDate > now ||
                             !subscription.IsApproved ||
                             subscription.PaymentStatus == PaymentStatusEnum.Pending))
                        .ToListAsync();

                    foreach (var subscription in subscriptions)
                    {
                        subscription.AllowAutomaticCardPayments = false;
                        subscription.IsAutomaticRenewal = false;
                        subscription.UpdatedAt = now;
                        subscription.UpdatedBy = currentAdminId;

                        if (subscription.IsApproved &&
                            subscription.PaymentStatus == PaymentStatusEnum.Success &&
                            subscription.EndDate > now)
                        {
                            // Keep the successful payment in the history, but end its access now.
                            subscription.EndDate = now;
                        }
                        else
                        {
                            // Match the existing rejection convention so cancelled requests remain
                            // available through the admin history query that ignores query filters.
                            subscription.IsApproved = false;
                            subscription.PaymentStatus = PaymentStatusEnum.Failed;
                            subscription.IsDeleted = true;
                            subscription.DeletedAt = now;
                            subscription.DeletedBy = currentAdminId;
                            if (subscription.EndDate > now)
                            {
                                subscription.EndDate = now;
                            }
                        }
                    }

                    cancelledSubscriptionCount = subscriptions.Count;
                }

                landlord.IsSubscriptionExempt = request.Enabled;
                var result = await _userManager.UpdateAsync(landlord);
                if (!result.Succeeded)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new
                    {
                        Message = string.Join(" ", result.Errors.Select(error => error.Description))
                    });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            return Ok(new
            {
                landlord.Id,
                landlord.IsSubscriptionExempt,
                CancelledSubscriptionCount = cancelledSubscriptionCount,
                Message = request.Enabled
                    ? "Subscription exemption enabled. Current subscriptions were ended and pending requests were cancelled."
                    : "Subscription exemption disabled for this landlord."
            });
        }

        [HttpGet("{userId}/overview")]
        [Authorize(Roles = "Admin,Landlord,Manager")]
        public async Task<IActionResult> GetOverview(string userId)
        {
            var isAdmin = User.IsInRole("Admin");
            if (!isAdmin && !await CanAccessUserOverviewAsync(userId))
            {
                return Forbid();
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound("User was not found.");
            }

            var roles = (await _userManager.GetRolesAsync(user)).ToList();
            var kyc = isAdmin
                ? await _context.LandlordKycProfiles
                    .AsNoTracking()
                    .Include(p => p.ReviewedBy)
                    .FirstOrDefaultAsync(p => p.UserId == user.Id)
                : null;
            var status = BuildStatusDto(user, roles, kyc);

            return Ok(new AdminUserOverviewDto
            {
                User = status,
                WhatsAppActivation = await BuildWhatsAppActivationAsync(user),
                Kyc = isAdmin ? BuildKycSummary(kyc, user.Id) : null,
                OtpCodes = isAdmin ? await BuildOtpDtosAsync(user) : new List<AdminUserOtpDto>(),
                Steps = BuildStepDtos(status)
            });
        }

        [HttpGet("{userId}/kyc-file/{key}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetKycFile(string userId, string key)
        {
            var profile = await _context.LandlordKycProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId);

            if (profile == null)
            {
                return NotFound("KYC profile was not found.");
            }

            var media = ResolveKycFile(profile, key);
            if (media == null)
            {
                return NotFound("KYC file was not found.");
            }

            var readUrl = await _kycFileStorageService.GetReadUrlAsync(media.Value.Path, TimeSpan.FromMinutes(20));
            if (string.IsNullOrWhiteSpace(readUrl))
            {
                return NotFound("KYC file content was not found.");
            }

            return Redirect(readUrl);
        }

        [HttpPost("{userId}/kyc/approve")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ApproveKyc(string userId, [FromBody] KycReviewRequest request)
        {
            return await ReviewKycAsync(userId, LandlordKycStatusEnum.Approved, request);
        }

        [HttpPost("{userId}/kyc/reject")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> RejectKyc(string userId, [FromBody] KycReviewRequest request)
        {
            return await ReviewKycAsync(userId, LandlordKycStatusEnum.Rejected, request);
        }

        [HttpGet("{userId}/otp-status")]
        [Authorize(Roles = "Admin")]
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
        [Authorize(Roles = "Admin")]
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
                user.PlatformTermsAccepted = false;
                user.PlatformTermsAcceptedAt = null;
                user.PlatformTermsSignatureName = null;
                user.PlatformTermsVersion = null;

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

                var kycFileUrls = await GetKycFileUrlsForUserAsync(userId);

                await _context.LandlordKycProfiles
                    .Where(p => p.UserId == userId)
                    .ExecuteDeleteAsync();

                await _kycFileStorageService.DeleteFilesAsync(kycFileUrls);

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
        [Authorize(Roles = "Admin")]
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
            var kycFileUrls = await GetKycFileUrlsForUserAsync(userId);

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
                await _kycFileStorageService.DeleteFilesAsync(kycFileUrls);

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

        private async Task<bool> CanAccessUserOverviewAsync(string targetUserId)
        {
            var currentUserId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(currentUserId)) return false;
            if (string.Equals(currentUserId, targetUserId, StringComparison.Ordinal)) return true;

            var accessiblePropertyIds = _context.Properties
                .AsNoTracking()
                .Where(property => !property.IsDeleted &&
                    (property.LandlordId == currentUserId ||
                     _context.PropertyManagerAssignments.Any(assignment =>
                         !assignment.IsDeleted &&
                         assignment.PropertyId == property.Id &&
                         assignment.ManagerId == currentUserId)))
                .Select(property => property.Id);

            return await _context.Properties.AsNoTracking().AnyAsync(property =>
                       accessiblePropertyIds.Contains(property.Id) && property.LandlordId == targetUserId)
                   || await _context.PropertyManagerAssignments.AsNoTracking().AnyAsync(assignment =>
                       !assignment.IsDeleted &&
                       accessiblePropertyIds.Contains(assignment.PropertyId) &&
                       assignment.ManagerId == targetUserId)
                   || await _context.ApartmentOwners.AsNoTracking().AnyAsync(assignment =>
                       !assignment.IsDeleted &&
                       assignment.Apartment != null &&
                       accessiblePropertyIds.Contains(assignment.Apartment.PropertyId) &&
                       assignment.OwnerId == targetUserId)
                   || await _context.TenancyMembers.AsNoTracking().AnyAsync(assignment =>
                       !assignment.IsDeleted &&
                       assignment.Tenancy != null &&
                       assignment.Tenancy.Apartment != null &&
                       accessiblePropertyIds.Contains(assignment.Tenancy.Apartment.PropertyId) &&
                       assignment.MemberId == targetUserId);
        }

        private async Task<IActionResult> ReviewKycAsync(string userId, LandlordKycStatusEnum status, KycReviewRequest? request)
        {
            var currentUserId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Forbid();
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound("User was not found.");
            }

            var profile = await _context.LandlordKycProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null)
            {
                return NotFound("KYC profile was not found.");
            }

            var rejectedKycFileUrls = new List<string>();
            var note = string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note.Trim();
            if (status == LandlordKycStatusEnum.Rejected)
            {
                if (string.IsNullOrWhiteSpace(note))
                {
                    return BadRequest("Provide a rejection reason so the landlord knows what to correct.");
                }

                ApplyKycRejectedFiles(profile, request);
                if (!HasRejectedKycFileFlags(profile))
                {
                    return BadRequest("Select at least one rejected KYC file or choose reject all.");
                }

                rejectedKycFileUrls = ClearRejectedKycFileReferences(profile);
            }
            else
            {
                ClearKycRejectionFlags(profile);
            }

            profile.Status = status;
            profile.ReviewedById = currentUserId;
            profile.ReviewedAt = DateTimeOffset.UtcNow;
            profile.ReviewNote = note;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            if (rejectedKycFileUrls.Count > 0)
            {
                await _kycFileStorageService.DeleteFilesAsync(rejectedKycFileUrls);
            }

            await SendKycReviewEmailAsync(user, status, note);

            return Ok(new { Message = status == LandlordKycStatusEnum.Approved ? "KYC approved." : "KYC rejected." });
        }

        private AdminUserVerificationStatusDto BuildStatusDto(ApplicationUser user, List<string> roles, LandlordKycProfile? kycProfile)
        {
            var nextStep = ResolveLandlordOnboardingStep(user, roles, kycProfile);
            return new AdminUserVerificationStatusDto
            {
                UserId = user.Id,
                Email = user.Email ?? string.Empty,
                FullName = user.FullName ?? string.Empty,
                CountryCode = user.CountryCode,
                CountryIsoCode = NormalizeCountryIsoCode(user.CountryIsoCode) ?? ResolveCountryIsoFromCountryCode(user.CountryCode),
                PhoneNumber = user.PhoneNumber,
                EmailConfirmed = user.EmailConfirmed,
                PhoneNumberConfirmed = user.PhoneNumberConfirmed,
                RequireMainPhoneVerification = RequireMainPhoneVerification,
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
                StripeConnectAccountId = user.StripeConnectAccountId ?? string.Empty,
                HasStripePayoutAccount = !string.IsNullOrWhiteSpace(user.StripeConnectAccountId),
                StripePayoutDetailsSubmitted = user.StripePayoutDetailsSubmitted,
                StripeChargesEnabled = user.StripeChargesEnabled,
                StripePayoutsEnabled = user.StripePayoutsEnabled,
                StripePayoutSetupComplete = IsStripePayoutSetupComplete(user),
                StripePayoutRequirementsSummary = user.StripePayoutRequirementsSummary ?? string.Empty,
                StripePayoutDisabledReason = user.StripePayoutDisabledReason ?? string.Empty,
                StripePayoutSetupStartedAt = user.StripePayoutSetupStartedAt,
                StripePayoutSetupCompletedAt = user.StripePayoutSetupCompletedAt,
                StripePayoutStatusUpdatedAt = user.StripePayoutStatusUpdatedAt,
                CreatedAt = user.CreatedAt,
                Roles = roles,
                NextOnboardingStep = nextStep,
                IsOnboardingComplete = nextStep == LandlordOnboardingSteps.Complete,
                IsSubscriptionExempt = user.IsSubscriptionExempt
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

            await _context.LandlordKycProfiles
                .Where(p => p.UserId == userId)
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

        private async Task<List<string>> GetKycFileUrlsForUserAsync(string userId)
        {
            var profile = await _context.LandlordKycProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId);

            return CollectKycFileUrls(profile);
        }

        private static List<string> CollectKycFileUrls(LandlordKycProfile? profile)
        {
            if (profile == null)
            {
                return new List<string>();
            }

            return new[]
                {
                    profile.FaceFrontPath,
                    profile.FaceRightPath,
                    profile.FaceLeftPath,
                    profile.DocumentFrontPath,
                    profile.DocumentBackPath
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct()
                .ToList();
        }

        private static List<string> ClearRejectedKycFileReferences(LandlordKycProfile profile)
        {
            var fileUrls = new List<string>();

            if (profile.RejectFaceFront)
            {
                AddKycFileUrl(fileUrls, profile.FaceFrontPath);
                profile.FaceFrontPath = string.Empty;
                profile.FaceFrontContentType = string.Empty;
                profile.FaceFrontOriginalFileName = string.Empty;
            }

            if (profile.RejectFaceRight)
            {
                AddKycFileUrl(fileUrls, profile.FaceRightPath);
                profile.FaceRightPath = string.Empty;
                profile.FaceRightContentType = string.Empty;
                profile.FaceRightOriginalFileName = string.Empty;
            }

            if (profile.RejectFaceLeft)
            {
                AddKycFileUrl(fileUrls, profile.FaceLeftPath);
                profile.FaceLeftPath = string.Empty;
                profile.FaceLeftContentType = string.Empty;
                profile.FaceLeftOriginalFileName = string.Empty;
            }

            if (profile.RejectDocumentFront)
            {
                AddKycFileUrl(fileUrls, profile.DocumentFrontPath);
                profile.DocumentFrontPath = string.Empty;
                profile.DocumentFrontContentType = string.Empty;
                profile.DocumentFrontOriginalFileName = string.Empty;
            }

            if (profile.RejectDocumentBack && RequiresDocumentBack(profile.DocumentType))
            {
                AddKycFileUrl(fileUrls, profile.DocumentBackPath);
                profile.DocumentBackPath = null;
                profile.DocumentBackContentType = null;
                profile.DocumentBackOriginalFileName = null;
            }

            return fileUrls.Distinct().ToList();
        }

        private static void AddKycFileUrl(List<string> fileUrls, string? fileUrl)
        {
            if (!string.IsNullOrWhiteSpace(fileUrl))
            {
                fileUrls.Add(fileUrl);
            }
        }

        private void ApplyKycRejectedFiles(LandlordKycProfile profile, KycReviewRequest? request)
        {
            var rejectAll = request?.RejectAllFiles == true;
            var documentBackAllowed = RequiresDocumentBack(profile.DocumentType) && !string.IsNullOrWhiteSpace(profile.DocumentBackPath);

            profile.RejectFaceFront = rejectAll || request?.RejectFaceFront == true;
            profile.RejectFaceRight = rejectAll || request?.RejectFaceRight == true;
            profile.RejectFaceLeft = rejectAll || request?.RejectFaceLeft == true;
            profile.RejectDocumentFront = rejectAll || request?.RejectDocumentFront == true;
            profile.RejectDocumentBack = documentBackAllowed && (rejectAll || request?.RejectDocumentBack == true);
        }

        private static void ClearKycRejectionFlags(LandlordKycProfile profile)
        {
            profile.RejectFaceFront = false;
            profile.RejectFaceRight = false;
            profile.RejectFaceLeft = false;
            profile.RejectDocumentFront = false;
            profile.RejectDocumentBack = false;
        }

        private static bool HasRejectedKycFileFlags(LandlordKycProfile profile)
        {
            return profile.RejectFaceFront
                || profile.RejectFaceRight
                || profile.RejectFaceLeft
                || profile.RejectDocumentFront
                || (profile.RejectDocumentBack && RequiresDocumentBack(profile.DocumentType));
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

        private static bool RequiresDocumentBack(KycDocumentTypeEnum documentType)
        {
            return documentType is KycDocumentTypeEnum.NationalId
                or KycDocumentTypeEnum.DriverLicense
                or KycDocumentTypeEnum.ResidencePermit
                or KycDocumentTypeEnum.Other;
        }

        private async Task SendKycReviewEmailAsync(ApplicationUser user, LandlordKycStatusEnum status, string? note)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return;
            }

            var isFrench = user.EmailLanguage == PlatformLanguage.French;
            var subject = isFrench
                ? (status == LandlordKycStatusEnum.Approved
                    ? "Votre vérification d’identité Lontsi Homes a été approuvée"
                    : "Votre vérification d’identité Lontsi Homes doit être corrigée")
                : (status == LandlordKycStatusEnum.Approved
                    ? "Your Lontsi Homes identity verification was approved"
                    : "Your Lontsi Homes identity verification needs correction");

            var link = status == LandlordKycStatusEnum.Approved
                ? BuildPortalUrl("/Properties")
                : BuildPortalUrl($"/Auth/LandlordKyc?email={Uri.EscapeDataString(user.Email)}");

            var lines = isFrench
                ? (status == LandlordKycStatusEnum.Approved
                    ? new[]
                    {
                        $"Bonjour {ResolveDisplayName(user)},",
                        string.Empty,
                        "Votre vérification d’identité a été approuvée.",
                        "Vous pouvez maintenant accéder à l’espace des propriétés :",
                        link,
                        string.Empty,
                        "Lontsi Homes"
                    }
                    : new[]
                    {
                        $"Bonjour {ResolveDisplayName(user)},",
                        string.Empty,
                        "Votre vérification d’identité a été refusée et doit être corrigée.",
                        $"Raison : {note}",
                        string.Empty,
                        "Utilisez ce lien pour téléverser les fichiers KYC corrigés :",
                        link,
                        string.Empty,
                        "Lontsi Homes"
                    })
                : status == LandlordKycStatusEnum.Approved
                ? new[]
                {
                    $"Hello {ResolveDisplayName(user)},",
                    string.Empty,
                    "Your identity verification has been approved.",
                    "You can now continue to the properties workspace:",
                    link,
                    string.Empty,
                    "Lontsi Homes"
                }
                : new[]
                {
                    $"Hello {ResolveDisplayName(user)},",
                    string.Empty,
                    "Your identity verification was rejected and needs correction.",
                    $"Reason: {note}",
                    string.Empty,
                    "Use this link to upload the corrected KYC file(s):",
                    link,
                    string.Empty,
                    "Lontsi Homes"
                };

            await _emailService.SendEmailAsync(user.Email, subject, string.Join(Environment.NewLine, lines));
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

        private static string ResolveDisplayName(ApplicationUser user)
        {
            return string.IsNullOrWhiteSpace(user.FullName) ? "there" : user.FullName.Trim();
        }

        private async Task<AdminWhatsAppActivationDto> BuildWhatsAppActivationAsync(ApplicationUser user)
        {
            var consents = await _context.UserCommunicationConsents.AsNoTracking()
                .Where(c => c.UserId == user.Id &&
                    c.Channel == CommunicationChannels.WhatsApp &&
                    c.Purpose == CommunicationPurposes.Transactional)
                .Select(c => new { c.Status, c.PhoneNumberE164 })
                .ToListAsync();
            var active = consents.Any(c => c.Status == CommunicationConsentStatuses.Granted &&
                c.PhoneNumberE164 == user.NormalizedWhatsAppPhoneNumber);
            var number = user.PendingWhatsAppPhoneNumber ?? user.WhatsAppPhoneNumber;
            var revoked = consents.Any(c => c.Status == CommunicationConsentStatuses.Revoked &&
                c.PhoneNumberE164 == number);
            var hash = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, "WhatsAppConsentOtpHashV1");
            var expiry = await _userManager.GetAuthenticationTokenAsync(user, OtpLoginProvider, "WhatsAppConsentOtpExpiryV1");
            return WhatsAppActivationStatus.Build(user, active, revoked,
                !string.IsNullOrWhiteSpace(hash), ParseUnixExpiry(expiry), DateTimeOffset.UtcNow);
        }

        private async Task<List<AdminUserOtpDto>> BuildOtpDtosAsync(ApplicationUser user)
        {
            var result = new List<AdminUserOtpDto>();
            foreach (var (codeToken, expiryToken) in OtpTokens)
            {
                // Profile WhatsApp activation has its own hashed OTP and consent lifecycle.
                // Never expose its hash (or a legacy code) as a readable test OTP.
                if (codeToken == "WhatsAppOtpCode") continue;
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

            var isCameroonLandlord = IsCameroonCountry(status.CountryIsoCode, status.CountryCode);
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

            var steps = new List<AdminUserOnboardingStepDto>
            {
                CreateStep(LandlordOnboardingSteps.Account, "Account created", "Name, email, and landlord role are registered.", !string.IsNullOrWhiteSpace(status.Email), status.NextOnboardingStep == LandlordOnboardingSteps.Account, status.Email),
                CreateStep(LandlordOnboardingSteps.Email, "Email verified", "The landlord confirms the email OTP.", status.EmailConfirmed, status.NextOnboardingStep == LandlordOnboardingSteps.Email, status.EmailConfirmed ? "Email confirmed." : "Waiting for email OTP."),
                CreateStep(LandlordOnboardingSteps.Country, "Country selected", "The landlord chooses the country for payment setup rules.", !string.IsNullOrWhiteSpace(status.CountryIsoCode) || !string.IsNullOrWhiteSpace(status.CountryCode), status.NextOnboardingStep == LandlordOnboardingSteps.Country, CountryDetails(status)),
                CreateStep(
                    LandlordOnboardingSteps.Phone,
                    "Primary phone verified",
                    "The landlord confirms the verification code for the primary phone.",
                    !string.IsNullOrWhiteSpace(status.PhoneNumber) && status.PhoneNumberConfirmed,
                    status.NextOnboardingStep == LandlordOnboardingSteps.Phone,
                    status.PhoneNumberConfirmed ? status.PhoneNumber : "Waiting for primary phone OTP.")
            };

            if (!status.RequireMainPhoneVerification)
            {
                steps.RemoveAll(step => step.Key == LandlordOnboardingSteps.Phone);
            }

            if (isCameroonLandlord)
            {
                steps.Add(CreateStep(LandlordOnboardingSteps.MobilePayments, "Mobile money configured", "Subscription and rent payout numbers are selected with MTN or Orange Money.", hasSubscriptionPaymentDetails && hasPayoutDetails, status.NextOnboardingStep == LandlordOnboardingSteps.MobilePayments, MobileMoneyDetails(status)));
                steps.Add(CreateStep(LandlordOnboardingSteps.MobilePaymentVerification, "Payment numbers verified", "Every distinct mobile transaction number is validated by OTP.", mobilePaymentVerificationComplete, status.NextOnboardingStep == LandlordOnboardingSteps.MobilePaymentVerification, MobileVerificationDetails(status)));
            }

            steps.Add(CreateStep(LandlordOnboardingSteps.Kyc, "Identity documents submitted", "The landlord uploads three face photos and ID document images for admin review.", status.IsKycSubmitted, status.NextOnboardingStep == LandlordOnboardingSteps.Kyc, KycDetails(status)));
            steps.Add(CreateStep("kyc-approval", "Identity review approved", "An admin approves the submitted identity information before payments are unlocked.", status.IsKycApproved, false, status.IsKycApproved ? "KYC approved." : $"KYC status: {status.KycStatus}."));
            steps.Add(CreateStep(LandlordOnboardingSteps.Contract, "Platform contract signed", "The landlord accepts the platform terms and signs with their full name.", status.PlatformTermsAccepted, status.NextOnboardingStep == LandlordOnboardingSteps.Contract, status.PlatformTermsAccepted ? $"Signed by {status.PlatformTermsSignatureName}" : "Waiting for signature."));
            steps.Add(CreateStep(LandlordOnboardingSteps.Complete, "Registration ready", "The landlord can continue into the authenticated workspace.", status.IsOnboardingComplete, status.NextOnboardingStep == LandlordOnboardingSteps.Complete, status.IsOnboardingComplete ? "All required steps are complete." : "Some verification work remains."));

            return steps;
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

        private bool IsOtpRequired(ApplicationUser user, string codeToken)
        {
            return codeToken switch
            {
                "ActivationOtpCode" => !string.IsNullOrWhiteSpace(user.Email),
                "PhoneOtpCode" => RequireMainPhoneVerification && !string.IsNullOrWhiteSpace(user.PhoneNumber),
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

        private static string CountryDetails(AdminUserVerificationStatusDto status)
        {
            if (string.IsNullOrWhiteSpace(status.CountryIsoCode) && string.IsNullOrWhiteSpace(status.CountryCode))
            {
                return "Waiting for country selection.";
            }

            if (string.IsNullOrWhiteSpace(status.CountryIsoCode))
            {
                return status.CountryCode ?? "Waiting for country selection.";
            }

            return string.IsNullOrWhiteSpace(status.CountryCode)
                ? status.CountryIsoCode
                : $"{status.CountryIsoCode} ({status.CountryCode})";
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

        private static string KycDetails(AdminUserVerificationStatusDto status)
        {
            if (!status.IsKycSubmitted)
            {
                return "Waiting for KYC upload.";
            }

            return $"KYC status: {status.KycStatus}. Manual admin review required.";
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

        private static LandlordKycSummaryDto? BuildKycSummary(LandlordKycProfile? profile, string userId)
        {
            if (profile == null)
            {
                return null;
            }

            var summary = new LandlordKycSummaryDto
            {
                HasProfile = true,
                DocumentType = profile.DocumentType,
                Status = profile.Status,
                SubmittedAt = profile.SubmittedAt,
                ReviewedAt = profile.ReviewedAt,
                ReviewedByName = profile.ReviewedBy?.FullName ?? profile.ReviewedBy?.Email,
                ReviewNote = profile.ReviewNote,
                RejectedFiles = BuildRejectedFiles(profile)
            };

            AddKycMediaIfPresent(summary, userId, "face-front", "Face - front", profile.FaceFrontPath, profile.FaceFrontOriginalFileName, profile.FaceFrontContentType);
            AddKycMediaIfPresent(summary, userId, "face-right", "Face - looking right", profile.FaceRightPath, profile.FaceRightOriginalFileName, profile.FaceRightContentType);
            AddKycMediaIfPresent(summary, userId, "face-left", "Face - looking left", profile.FaceLeftPath, profile.FaceLeftOriginalFileName, profile.FaceLeftContentType);
            AddKycMediaIfPresent(summary, userId, "document-front", "Document front", profile.DocumentFrontPath, profile.DocumentFrontOriginalFileName, profile.DocumentFrontContentType);
            if (!string.IsNullOrWhiteSpace(profile.DocumentBackPath))
            {
                AddKycMedia(summary, userId, "document-back", "Document back", profile.DocumentBackOriginalFileName ?? string.Empty, profile.DocumentBackContentType ?? string.Empty);
            }

            return summary;
        }

        private static void AddKycMediaIfPresent(
            LandlordKycSummaryDto summary,
            string userId,
            string key,
            string label,
            string? path,
            string originalFileName,
            string contentType)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                AddKycMedia(summary, userId, key, label, originalFileName, contentType);
            }
        }

        private static void AddKycMedia(
            LandlordKycSummaryDto summary,
            string userId,
            string key,
            string label,
            string originalFileName,
            string contentType)
        {
            summary.Media.Add(new LandlordKycMediaDto
            {
                Key = key,
                Label = label,
                OriginalFileName = originalFileName,
                ContentType = contentType,
                IsImage = !string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
                Url = $"AdminUsers/{Uri.EscapeDataString(userId)}/kyc-file/{Uri.EscapeDataString(key)}"
            });
        }

        private static (string Path, string ContentType, string OriginalFileName)? ResolveKycFile(LandlordKycProfile profile, string key)
        {
            return key switch
            {
                "face-front" => ResolveKycFileIfPresent(profile.FaceFrontPath, profile.FaceFrontContentType, profile.FaceFrontOriginalFileName),
                "face-right" => ResolveKycFileIfPresent(profile.FaceRightPath, profile.FaceRightContentType, profile.FaceRightOriginalFileName),
                "face-left" => ResolveKycFileIfPresent(profile.FaceLeftPath, profile.FaceLeftContentType, profile.FaceLeftOriginalFileName),
                "document-front" => ResolveKycFileIfPresent(profile.DocumentFrontPath, profile.DocumentFrontContentType, profile.DocumentFrontOriginalFileName),
                "document-back" => ResolveKycFileIfPresent(profile.DocumentBackPath, profile.DocumentBackContentType, profile.DocumentBackOriginalFileName),
                _ => null
            };
        }

        private static (string Path, string ContentType, string OriginalFileName)? ResolveKycFileIfPresent(
            string? path,
            string? contentType,
            string? originalFileName)
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : (path, contentType ?? string.Empty, originalFileName ?? string.Empty);
        }

        private string ResolveLandlordOnboardingStep(
            ApplicationUser user,
            IReadOnlyCollection<string> roles,
            LandlordKycProfile? kycProfile)
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

            if (string.IsNullOrWhiteSpace(user.CountryCode) && string.IsNullOrWhiteSpace(user.CountryIsoCode))
            {
                return LandlordOnboardingSteps.Country;
            }

            if (RequireMainPhoneVerification && (string.IsNullOrWhiteSpace(user.PhoneNumber) || !user.PhoneNumberConfirmed))
            {
                return LandlordOnboardingSteps.Phone;
            }

            if (!IsCameroonCountry(user.CountryIsoCode, user.CountryCode))
            {
                if (kycProfile?.Status is not (LandlordKycStatusEnum.Submitted or LandlordKycStatusEnum.Approved))
                {
                    return LandlordOnboardingSteps.Kyc;
                }

                return user.PlatformTermsAccepted
                    ? LandlordOnboardingSteps.Complete
                    : LandlordOnboardingSteps.Contract;
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

            if (kycProfile?.Status is not (LandlordKycStatusEnum.Submitted or LandlordKycStatusEnum.Approved))
            {
                return LandlordOnboardingSteps.Kyc;
            }

            if (!user.PlatformTermsAccepted)
            {
                return LandlordOnboardingSteps.Contract;
            }

            return LandlordOnboardingSteps.Complete;
        }

        private static bool IsStripePayoutSetupComplete(ApplicationUser user)
        {
            return !string.IsNullOrWhiteSpace(user.StripeConnectAccountId) &&
                   user.StripePayoutDetailsSubmitted &&
                   user.StripeChargesEnabled &&
                   user.StripePayoutsEnabled;
        }

        private static string? NormalizeCountryIsoCode(string? countryIsoCode)
        {
            var trimmed = (countryIsoCode ?? string.Empty).Trim().ToUpperInvariant();
            return trimmed.Length == 2 && trimmed.All(char.IsLetter) ? trimmed : null;
        }

        private static string? ResolveCountryIsoFromCountryCode(string? countryCode)
        {
            var digits = new string((countryCode ?? string.Empty).Where(char.IsDigit).ToArray());
            return digits switch
            {
                "1" => "CA",
                "237" => "CM",
                "44" => "GB",
                "33" => "FR",
                "32" => "BE",
                "49" => "DE",
                "234" => "NG",
                "225" => "CI",
                "233" => "GH",
                "27" => "ZA",
                "254" => "KE",
                "971" => "AE",
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
            var digits = new string((countryCode ?? string.Empty).Where(char.IsDigit).ToArray());
            return string.Equals(digits, "237", StringComparison.Ordinal);
        }
    }
}
