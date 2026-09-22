using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using Common.CommunicationModels;
using LontsiHomes.API.Models.Entities;
using Common.Enums;
using LontsiHomes.API.Services.Storage;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using LontsiHomes.API.Helpers;
using LontsiHomes.API.Services.Permissions;

namespace LontsiHomes.API.Controllers
{
    /// <summary>
    /// Provides endpoints for uploading, listing and deleting documents.  Files are stored
    /// in Azure Blob Storage and metadata is persisted in the database.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IStorageService _storageService;
        private readonly IManagerPermissionService _permissionService;

        public DocumentsController(
            ApplicationDbContext context,
            IStorageService storageService,
            IManagerPermissionService permissionService)
        {
            _context = context;
            _storageService = storageService;
            _permissionService = permissionService;
        }

        [HttpPost("property/{propertyId}")]
        [Authorize]
        public async Task<IActionResult> UploadForProperty([FromRoute] int propertyId, [FromForm] UploadDocumentRequest request)
        {
            try
            {
                if (request.File == null || request.File.Length == 0) return BadRequest("File is required.");
                if (!ValidateUpload(request.File, request.Type, out var propertyValidationMessage)) return BadRequest(propertyValidationMessage);
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                if (property == null) return NotFound("Property not found.");

                var canWrite = await _permissionService.HasPropertyPermissionAsync(
                    userId, propertyId, ManagerPermission.UploadDocuments, User.IsInRole("Admin"));
                if (!canWrite) return Forbid();
                if (!User.IsInRole("Admin") && !await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, property.LandlordId))
                    return SubscriptionRequired();

                var extension = Path.GetExtension(request.File.FileName);
                var blobName = $"property-{propertyId}-{Guid.NewGuid()}{extension}";
                string blobUrl;
                using (var stream = request.File.OpenReadStream())
                {
                    blobUrl = await _storageService.UploadFileAsync(stream, blobName, request.File.ContentType);
                }

                var document = new Document
                {
                    UserId = userId,
                    FileName = request.File.FileName,
                    BlobUrl = blobUrl,
                    DocumentType = request.Type,
                    PropertyId = propertyId,
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };
                _context.Documents.Add(document);
                await _context.SaveChangesAsync();

                return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, await DocumentHelpers.ToDtoAsync(document, _storageService));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("apartment/{apartmentId}")]
        [Authorize]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadForApartment(int apartmentId, [FromForm] UploadApartmentDocumentRequest request)
        {
            try
            {
                if (request?.File == null || request.File.Length == 0)
                    return BadRequest("File is required.");

                if (!ValidateUpload(request.File, request.DocumentType, out var uploadValidationMessage))
                    return BadRequest(uploadValidationMessage);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId);

                if (apartment == null)
                    return NotFound("Apartment not found.");

                var canWrite =
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, apartmentId, ManagerPermission.ManageApartmentDocuments, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();
                if (!User.IsInRole("Admin") && apartment.Property != null &&
                    !await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, apartment.Property.LandlordId))
                    return SubscriptionRequired();

                var file = request.File;
                var extension = Path.GetExtension(file.FileName);
                var blobName = $"apartment-{apartmentId}-{Guid.NewGuid()}{extension}";

                string blobUrl;
                using (var stream = file.OpenReadStream())
                {
                    blobUrl = await _storageService.UploadFileAsync(stream, blobName, file.ContentType);
                }

                var document = new Document
                {
                    UserId = userId,
                    FileName = file.FileName,
                    BlobUrl = blobUrl,
                    DocumentType = request.DocumentType,
                    ApartmentId = apartmentId,
                    PropertyId = apartment.PropertyId,
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };

                _context.Documents.Add(document);
                await _context.SaveChangesAsync();

                return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, await DocumentHelpers.ToDtoAsync(document, _storageService));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("tenancy/{tenancyId}")]
        [Authorize]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadForTenancy(int tenancyId, [FromForm] UploadTenancyDocumentRequest request)
        {
            try
            {
                if (request?.File == null || request.File.Length == 0)
                    return BadRequest("File is required.");

                if (!ValidateUpload(request.File, request.DocumentType, out var uploadValidationMessage))
                    return BadRequest(uploadValidationMessage);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);

                if (tenancy == null)
                    return NotFound("Tenancy not found.");

                var apartment = tenancy.Apartment;
                if (apartment == null)
                    return NotFound("Apartment not found.");

                var canWrite =
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancyId, ManagerPermission.UploadLeaseDocuments, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartment.Id && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();
                if (!User.IsInRole("Admin") && apartment.Property != null &&
                    !await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, apartment.Property.LandlordId))
                    return SubscriptionRequired();

                var file = request.File;
                var extension = Path.GetExtension(file.FileName);
                var blobName = $"tenancy-{tenancyId}-{Guid.NewGuid()}{extension}";

                string blobUrl;
                using (var stream = file.OpenReadStream())
                {
                    blobUrl = await _storageService.UploadFileAsync(stream, blobName, file.ContentType);
                }

                var document = new Document
                {
                    UserId = userId,
                    FileName = file.FileName,
                    BlobUrl = blobUrl,
                    DocumentType = request.DocumentType,
                    TenancyId = tenancyId,
                    ApartmentId = apartment.Id,
                    PropertyId = apartment.PropertyId,
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };

                _context.Documents.Add(document);
                await _context.SaveChangesAsync();

                return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, await DocumentHelpers.ToDtoAsync(document, _storageService));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("{id}")]
        [Authorize]
        public async Task<IActionResult> GetDocument(int id)
        {
            try
            {
                var document = await _context.Documents
                    .Include(d => d.Property)
                    .Include(d => d.Apartment)
                    .Include(d => d.Tenancy)
                    .FirstOrDefaultAsync(d => d.Id == id);
                if (document == null) return NotFound("Document not found.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var isAdmin = User.IsInRole("Admin");
                var hasAccess = isAdmin || document.UserId == userId;
                if (!hasAccess && document.PropertyId.HasValue)
                {
                    hasAccess = await _permissionService.HasPropertyPermissionAsync(
                        userId, document.PropertyId.Value, ManagerPermission.ViewDocuments, isAdmin);
                }
                if (!hasAccess && document.ApartmentId.HasValue)
                {
                    var apartmentId = document.ApartmentId.Value;
                    var apartment = await _context.Apartments.FirstOrDefaultAsync(a => a.Id == apartmentId);
                    if (apartment != null)
                    {
                        hasAccess = await _permissionService.HasApartmentPermissionAsync(
                            userId, apartmentId, ManagerPermission.ViewDocuments, isAdmin);
                    }
                }
                if (!hasAccess && document.TenancyId.HasValue)
                {
                    var tenancyId = document.TenancyId.Value;
                    var tenancy = await _context.Tenancies
                        .Include(t => t.Apartment)
                        .FirstOrDefaultAsync(t => t.Id == tenancyId);

                    if (tenancy?.Apartment != null)
                    {
                        hasAccess = await _permissionService.HasTenancyPermissionAsync(
                            userId, tenancyId, ManagerPermission.ViewLeaseDocuments, isAdmin);
                    }
                }
                if (!hasAccess) return Forbid();

                return Ok(await DocumentHelpers.ToDtoAsync(document, _storageService));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("property/{propertyId}")]
        [Authorize]
        public async Task<IActionResult> GetDocumentsForProperty(int propertyId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                if (property == null) return NotFound("Property not found.");
                var hasAccess = await _permissionService.HasPropertyPermissionAsync(
                    userId, propertyId, ManagerPermission.ViewDocuments, User.IsInRole("Admin"));
                if (!hasAccess) return Forbid();

                var documents = await _context.Documents
                    .Where(d => d.PropertyId == propertyId)
                    .ToListAsync();

                return Ok(await DocumentHelpers.ToDtosAsync(documents, _storageService));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("apartment/{apartmentId}")]
        [Authorize]
        public async Task<IActionResult> GetDocumentsForApartment(int apartmentId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId);
                if (apartment == null) return NotFound("Apartment not found.");

                var hasAccess =
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, apartmentId, ManagerPermission.ViewDocuments, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId) ||
                    await _context.Tenancies.AnyAsync(t =>
                        t.ApartmentId == apartmentId &&
                        t.Members.Any(mm => !mm.IsDeleted && mm.MemberId == userId));
                if (!hasAccess) return Forbid();

                var documents = await _context.Documents
                    .Where(d => d.ApartmentId == apartmentId)
                    .ToListAsync();

                return Ok(await DocumentHelpers.ToDtosAsync(documents, _storageService));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("tenancy/{tenancyId}")]
        [Authorize]
        public async Task<IActionResult> GetDocumentsForTenancy(int tenancyId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);
                if (tenancy == null) return NotFound("Tenancy not found.");
                var apartment = tenancy.Apartment;
                if (apartment == null) return NotFound("Apartment not found.");

                var hasAccess =
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancyId, ManagerPermission.ViewLeaseDocuments, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartment.Id && o.OwnerId == userId) ||
                    await _context.TenancyMembers.AnyAsync(m => m.TenancyId == tenancyId && !m.IsDeleted && m.MemberId == userId);
                if (!hasAccess) return Forbid();

                var documents = await _context.Documents
                    .Where(d => d.TenancyId == tenancyId)
                    .ToListAsync();

                return Ok(await DocumentHelpers.ToDtosAsync(documents, _storageService));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        [Authorize]
        public async Task<IActionResult> DeleteDocument(int id)
        {
            try
            {
                var document = await _context.Documents
                    .Include(d => d.Apartment)
                    .ThenInclude(a => a.Property)
                    .Include(d => d.Tenancy)
                    .FirstOrDefaultAsync(d => d.Id == id);
                if (document == null) return NotFound("Document not found.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var isAdmin = User.IsInRole("Admin");
                var isManager = User.IsInRole("Manager") && !isAdmin;
                var canDelete = document.TenancyId.HasValue
                    ? await _permissionService.HasTenancyPermissionAsync(
                        userId, document.TenancyId.Value, ManagerPermission.DeleteLeaseDocuments, isAdmin)
                    : document.ApartmentId.HasValue
                        ? await _permissionService.HasApartmentPermissionAsync(
                            userId, document.ApartmentId.Value, ManagerPermission.ManageApartmentDocuments, isAdmin)
                        : document.PropertyId.HasValue && await _permissionService.HasPropertyPermissionAsync(
                            userId, document.PropertyId.Value, ManagerPermission.DeleteDocuments, isAdmin);

                // Managers must always possess the explicit delete permission. Other account types
                // retain the legacy ability to delete a file they uploaded themselves.
                if (!canDelete && !isManager && document.UserId == userId)
                    canDelete = true;

                if (!canDelete && document.ApartmentId.HasValue)
                {
                    canDelete = await _context.ApartmentOwners.AnyAsync(owner =>
                        !owner.IsDeleted &&
                        owner.ApartmentId == document.ApartmentId.Value &&
                        owner.OwnerId == userId &&
                        owner.Permission == PermissionLevelEnum.ReadWrite);
                }

                if (!canDelete && document.TenancyId.HasValue)
                {
                    var apartmentId = await _context.Tenancies
                        .Where(tenancy => tenancy.Id == document.TenancyId.Value)
                        .Select(tenancy => (int?)tenancy.ApartmentId)
                        .FirstOrDefaultAsync();
                    if (apartmentId.HasValue)
                    {
                        canDelete = await _context.ApartmentOwners.AnyAsync(owner =>
                            !owner.IsDeleted &&
                            owner.ApartmentId == apartmentId.Value &&
                            owner.OwnerId == userId &&
                            owner.Permission == PermissionLevelEnum.ReadWrite);
                    }
                }
                if (!canDelete) return Forbid();

                document.IsDeleted = true;
                document.DeletedBy = userId;
                document.DeletedAt = DateTime.UtcNow;
                _context.Documents.Update(document);
                await _context.SaveChangesAsync();
                try
                {
                    await _storageService.DeleteFileAsync(document.BlobUrl);
                }
                catch
                {
                }
                return Ok(new { Message = "Document deleted." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private ObjectResult SubscriptionRequired()
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, new
            {
                Code = "SUBSCRIPTION_PAYMENT_REQUIRED",
                Message = PaymentAvailabilityHelper.SubscriptionRequiredMessage
            });
        }

        private static bool ValidateUpload(IFormFile file, DocumentTypeEnum type, out string errorMessage)
        {
            errorMessage = string.Empty;
            var contentType = file.ContentType ?? string.Empty;
            var extension = Path.GetExtension(file.FileName ?? string.Empty);

            if (type == DocumentTypeEnum.PropertyImage || type == DocumentTypeEnum.ApartmentImage)
            {
                var isImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    || new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" }.Contains(extension, StringComparer.OrdinalIgnoreCase);

                if (!isImage)
                {
                    errorMessage = "Only image files are allowed for image sections.";
                    return false;
                }

                return true;
            }

            var isPdf = string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

            if (!isPdf)
            {
                errorMessage = "Only PDF files are allowed in this document section.";
                return false;
            }

            return true;
        }
    }
}



