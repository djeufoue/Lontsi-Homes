using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using Common.Enums;
using System.Security.Claims;

using RentHub.API.Helpers;

namespace RentHub.API.Controllers
{
    /// <summary>
    /// Provides endpoints for tenants to request tenancy extensions and for landlords/managers
    /// to approve or reject these requests.
    /// </summary>
    [ApiController]
    [Route("api/tenancies/{tenancyId}/extension-requests")]
    public class TenancyExtensionRequestsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public TenancyExtensionRequestsController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Submits a request to extend a tenancy.  Only the tenant associated with the tenancy
        /// may submit an extension request.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateRequest(int tenancyId, [FromBody] ExtendTenancyRequest request)
        {
            try
            {
                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);
                if (tenancy == null) return NotFound("Tenancy not found.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Only the tenant may request extension
                if (tenancy.TenantId != userId)
                {
                    return Forbid();
                }
                // Validate proposed date
                if (request.NewEndDate <= tenancy.StartDate)
                {
                    return BadRequest("Proposed end date must be after tenancy start date.");
                }
                var extensionRequest = new TenancyExtensionRequest
                {
                    TenancyId = tenancyId,
                    RequestedById = userId,
                    ProposedEndDate = request.NewEndDate,
                    Status = TenancyExtensionStatusEnum.Pending,
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };
                _context.TenancyExtensionRequests.Add(extensionRequest);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Extension request submitted.", RequestId = extensionRequest.Id });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Approves an extension request.  Only the landlord or an authorized manager with
        /// write permission may approve.  Approval updates the tenancy end date.
        /// </summary>
        [HttpPut("{requestId}/approve")]
        [Authorize]
        public async Task<IActionResult> ApproveRequest(int tenancyId, int requestId)
        {
            try
            {
                var request = await _context.TenancyExtensionRequests
                    .Include(er => er.Tenancy)
                    .ThenInclude(t => t.Apartment!.Property)
                    .FirstOrDefaultAsync(er => er.Id == requestId && er.TenancyId == tenancyId);
                if (request == null) return NotFound("Extension request not found.");
                if (request.Status != TenancyExtensionStatusEnum.Pending)
                {
                    return BadRequest("Extension request is not in pending state.");
                }
                var tenancy = request.Tenancy;
                if (tenancy == null) return NotFound("Tenancy not found.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Determine if user can approve: landlord or manager/owner with write permission
                bool isLandlord = tenancy.Apartment!.Property!.LandlordId == userId;
                bool canWrite = false;
                if (isLandlord)
                {
                    canWrite = true;
                }
                else
                {
                    var ownerWrite = await _context.ApartmentOwners
                        .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    var managerWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    canWrite = ownerWrite || managerWrite;
                }
                if (!canWrite)
                {
                    return Forbid();
                }
                // Approve: update tenancy end date and request status
                tenancy.EndDate = request.ProposedEndDate;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = DateTime.UtcNow;
                request.Status = TenancyExtensionStatusEnum.Approved;
                request.ApprovedById = userId;
                request.ApprovedAt = DateTime.UtcNow;
                request.UpdatedBy = userId;
                request.UpdatedAt = DateTime.UtcNow;
                _context.Tenancies.Update(tenancy);
                _context.TenancyExtensionRequests.Update(request);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Extension request approved.", TenancyId = tenancy.Id, NewEndDate = tenancy.EndDate });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Rejects an extension request.  Only the landlord or an authorized manager with write
        /// permission may reject.  The tenancy end date is not modified.
        /// </summary>
        [HttpPut("{requestId}/reject")]
        [Authorize]
        public async Task<IActionResult> RejectRequest(int tenancyId, int requestId)
        {
            try
            {
                var request = await _context.TenancyExtensionRequests
                    .Include(er => er.Tenancy)
                    .ThenInclude(t => t.Apartment!.Property)
                    .FirstOrDefaultAsync(er => er.Id == requestId && er.TenancyId == tenancyId);
                if (request == null) return NotFound("Extension request not found.");
                if (request.Status != TenancyExtensionStatusEnum.Pending)
                {
                    return BadRequest("Extension request is not in pending state.");
                }
                var tenancy = request.Tenancy;
                if (tenancy == null) return NotFound("Tenancy not found.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Determine if user can reject: landlord or manager/owner with write permission
                bool isLandlord = tenancy.Apartment!.Property!.LandlordId == userId;
                bool canWrite = false;
                if (isLandlord)
                {
                    canWrite = true;
                }
                else
                {
                    var ownerWrite = await _context.ApartmentOwners
                        .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    var managerWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    canWrite = ownerWrite || managerWrite;
                }
                if (!canWrite)
                {
                    return Forbid();
                }
                request.Status = TenancyExtensionStatusEnum.Rejected;
                request.ApprovedById = userId;
                request.ApprovedAt = DateTime.UtcNow;
                request.UpdatedBy = userId;
                request.UpdatedAt = DateTime.UtcNow;
                _context.TenancyExtensionRequests.Update(request);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Extension request rejected." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}
