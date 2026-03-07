using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using Common.Enums;
using Common.CommunicationModels;
using System.Security.Claims;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/tenancies/{tenancyId}/members")]
    public class TenancyMembersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public TenancyMembersController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Adds a member to a tenancy.  Only the landlord associated with the tenancy may add members.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> AddMember(int tenancyId, [FromBody] AddTenancyMemberRequest request)
        {
            try
            {
                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment!.Property)
                    .Include(t => t.Members)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);
                if (tenancy == null) return NotFound("Tenancy not found.");

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Only landlord can add members
                if (tenancy.Apartment!.Property!.LandlordId != userId)
                {
                    return Forbid();
                }
                // Use the MaxMembers limit from the tenancy entity
                if (tenancy.Members.Count >= tenancy.MaxMembers)
                {
                    return BadRequest($"A tenancy cannot have more than {tenancy.MaxMembers} members.");
                }
                // Validate that the new member is not already part of the tenancy
                if (tenancy.Members.Any(tm => tm.MemberId == request.MemberId))
                {
                    return BadRequest("User is already a member of this tenancy.");
                }
                // Create new member entity
                var member = new TenancyMember
                {
                    TenancyId = tenancyId,
                    MemberId = request.MemberId,
                    Role = request.Role,
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };
                _context.TenancyMembers.Add(member);
                await _context.SaveChangesAsync();
                // Map to DTO
                var dto = new TenancyMemberDto
                {
                    Id = member.Id,
                    MemberId = member.MemberId,
                    Role = member.Role.ToString(),
                    UserName = member.Member != null ? member.Member.UserName ?? string.Empty : string.Empty
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
                var dtos = tenancy.Members.Select(m => new TenancyMemberDto
                {
                    Id = m.Id,
                    MemberId = m.MemberId,
                    Role = m.Role.ToString(),
                    UserName = m.Member != null ? m.Member.UserName ?? string.Empty : string.Empty
                }).ToList();
                return Ok(dtos);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}