using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Models.Entities;
using Common.CommunicationModels;
using System.Security.Claims;

using LontsiHomes.API.Helpers;
using LontsiHomes.API.Services.Users;
using LontsiHomes.API.Services.Permissions;
using Common.Enums;

namespace LontsiHomes.API.Controllers
{
    [ApiController]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Route("api/[controller]")]
    public class TenancyMembersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserOnboardingService _userOnboardingService;
        private readonly IManagerPermissionService _permissionService;

        public TenancyMembersController(
            ApplicationDbContext context,
            IUserOnboardingService userOnboardingService,
            IManagerPermissionService permissionService)
        {
            _context = context;
            _userOnboardingService = userOnboardingService;
            _permissionService = permissionService;
        }

        /// <summary>
        /// Adds a member to a tenancy. Only the landlord associated with the tenancy may add members.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> AddMember(int tenancyId, [FromBody] AddTenancyMemberRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment!.Property)
                    .Include(t => t.Members)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);

                if (tenancy == null) return NotFound("Tenancy not found.");

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                if (!await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancyId, ManagerPermission.AddTenancyMember, User.IsInRole("Admin")))
                    return Forbid();

                if (!await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, tenancy.Apartment.Property.LandlordId))
                {
                    return StatusCode(StatusCodes.Status402PaymentRequired, new
                    {
                        Code = "SUBSCRIPTION_PAYMENT_REQUIRED",
                        Message = PaymentAvailabilityHelper.SubscriptionRequiredMessage
                    });
                }

                if (tenancy.Members.Count(m => !m.IsDeleted) >= tenancy.MaxMembers)
                    return BadRequest($"A tenancy cannot have more than {tenancy.MaxMembers} members.");

                var email = request.Email.Trim();
                var memberUser = (await _userOnboardingService.EnsureUserAsync(
                    email,
                    request.FullName,
                    request.CountryCode,
                    request.PhoneNumber,
                    request.WhatsAppPhoneNumber,
                    "Tenant")).User;

                if (tenancy.Members.Any(tm => !tm.IsDeleted && tm.MemberId == memberUser.Id))
                    return BadRequest("User is already a member of this tenancy.");

                if (request.Role == Common.Enums.TenancyMemberRoleEnum.MainTenant &&
                    tenancy.Members.Any(tm => !tm.IsDeleted && tm.Role == Common.Enums.TenancyMemberRoleEnum.MainTenant))
                {
                    return BadRequest("This tenancy already has a primary tenant.");
                }

                var member = new TenancyMember
                {
                    TenancyId = tenancyId,
                    MemberId = memberUser.Id,
                    Role = request.Role,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.TenancyMembers.Add(member);

                await _context.SaveChangesAsync();

                var dto = new TenancyMemberDto
                {
                    Id = member.Id,
                    TenancyId = member.TenancyId,
                    MemberId = member.MemberId,
                    Role = member.Role.ToString(),
                    FullName = memberUser.FullName ?? string.Empty,
                    Email = memberUser.Email ?? string.Empty,
                    CountryCode = memberUser.CountryCode ?? string.Empty,
                    PhoneNumber = memberUser.PhoneNumber ?? string.Empty,
                    WhatsAppPhoneNumber = memberUser.WhatsAppPhoneNumber ?? string.Empty,
                    EmailConfirmed = memberUser.EmailConfirmed,
                    WhatsAppPhoneVerified = memberUser.IsWhatsAppPhoneVerified,
                    CreatedAt = member.CreatedAt
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Lists members of a tenancy.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetMembers(int tenancyId)
        {
            try
            {
                var tenancy = await _context.Tenancies
                    .Include(t => t.Members)
                    .ThenInclude(tm => tm.Member)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);

                if (tenancy == null) return NotFound();

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
                var isMember = tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == userId);
                if (!isMember && !await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancyId, ManagerPermission.ViewTenancyMembers, User.IsInRole("Admin")))
                    return Forbid();

                var dtos = tenancy.Members
                    .Where(m => !m.IsDeleted)
                    .Select(m => new TenancyMemberDto
                    {
                        Id = m.Id,
                        TenancyId = m.TenancyId,
                        MemberId = m.MemberId,
                        Role = m.Role.ToString(),
                        FullName = m.Member?.FullName ?? string.Empty,
                        Email = m.Member?.Email ?? string.Empty,
                        CountryCode = m.Member?.CountryCode ?? string.Empty,
                        PhoneNumber = m.Member?.PhoneNumber ?? string.Empty,
                        WhatsAppPhoneNumber = m.Member?.WhatsAppPhoneNumber ?? string.Empty,
                        EmailConfirmed = m.Member?.EmailConfirmed ?? false,
                        WhatsAppPhoneVerified = m.Member?.IsWhatsAppPhoneVerified ?? false,
                        CreatedAt = m.CreatedAt
                    })
                    .ToList();

                return Ok(dtos);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}







