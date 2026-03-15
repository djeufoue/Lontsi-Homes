using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using Common.Enums;
using RentHub.API.Services.Storage;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using RentHub.API.Helpers;

namespace RentHub.API.Controllers
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

        public DocumentsController(ApplicationDbContext context, IStorageService storageService)
        {
            _context = context;
            _storageService = storageService;
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

                var canWrite = property.LandlordId == userId || await _context.PropertyManagerAssignments
                    .AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                if (!canWrite) return Forbid();

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

                var canWrite = apartment.Property?.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

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

                var canWrite = apartment.Property?.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartment.Id && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

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

                var hasAccess = document.UserId == userId;
                if (document.PropertyId.HasValue)
                {
                    var propertyId = document.PropertyId.Value;
                    var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                    if (property != null)
                    {
                        if (property.LandlordId == userId) hasAccess = true;
                        if (!hasAccess)
                            hasAccess = await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId);
                        if (!hasAccess)
                            hasAccess = await _context.ApartmentOwners.Include(o => o.Apartment).AnyAsync(o => o.Apartment!.PropertyId == propertyId && o.OwnerId == userId);
                    }
                }
                if (!hasAccess && document.ApartmentId.HasValue)
                {
                    var apartmentId = document.ApartmentId.Value;
                    var apartment = await _context.Apartments.Include(a => a.Property).FirstOrDefaultAsync(a => a.Id == apartmentId);
                    if (apartment != null)
                    {
                        if (apartment.Property?.LandlordId == userId) hasAccess = true;
                        if (!hasAccess)
                            hasAccess = await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId);
                        if (!hasAccess)
                            hasAccess = await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId);
                        if (!hasAccess)
                            hasAccess = await _context.Tenancies.AnyAsync(t =>
                                t.ApartmentId == apartmentId &&
                                t.Members.Any(mm => !mm.IsDeleted && mm.MemberId == userId));
                    }
                }
                if (!hasAccess && document.TenancyId.HasValue)
                {
                    var tenancyId = document.TenancyId.Value;
                    var tenancy = await _context.Tenancies.Include(t => t.Apartment!.Property).FirstOrDefaultAsync(t => t.Id == tenancyId);

                    if (tenancy != null)
                    {
                        if (tenancy.Apartment!.Property!.LandlordId == userId) hasAccess = true;
                        if (!hasAccess)
                            hasAccess = await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId);
                        if (!hasAccess)
                            hasAccess = await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId);
                        if (!hasAccess && await _context.TenancyMembers.AnyAsync(m => m.TenancyId == tenancyId && !m.IsDeleted && m.MemberId == userId)) hasAccess = true;
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
                var hasAccess = property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId) ||
                    await _context.ApartmentOwners.Include(o => o.Apartment).AnyAsync(o => o.Apartment!.PropertyId == propertyId && o.OwnerId == userId);
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

                var hasAccess = apartment.Property?.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId) ||
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

                var hasAccess = apartment.Property?.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId) ||
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
                bool canDelete = document.UserId == userId;
                if (!canDelete && document.PropertyId.HasValue)
                {
                    var propertyId = document.PropertyId.Value;
                    var property = document.Property ?? await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                    if (property != null && property.LandlordId == userId)
                    {
                        canDelete = true;
                    }
                    if (!canDelete)
                    {
                        canDelete = await _context.PropertyManagerAssignments
                            .AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    }
                }
                if (!canDelete && document.ApartmentId.HasValue)
                {
                    var apartmentId = document.ApartmentId.Value;
                    var apartment = document.Apartment ?? await _context.Apartments
                        .Include(a => a.Property)
                        .FirstOrDefaultAsync(a => a.Id == apartmentId);
                    if (apartment != null && apartment.Property?.LandlordId == userId)
                    {
                        canDelete = true;
                    }
                    if (!canDelete)
                    {
                        canDelete = await _context.PropertyManagerAssignments
                            .AnyAsync(m => m.PropertyId == apartment!.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    }
                    if (!canDelete)
                    {
                        canDelete = await _context.ApartmentOwners
                            .AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    }
                }
                if (!canDelete && document.TenancyId.HasValue)
                {
                    var tenancyId = document.TenancyId.Value;
                    var tenancy = document.Tenancy ?? await _context.Tenancies
                        .Include(t => t.Apartment!.Property)
                        .FirstOrDefaultAsync(t => t.Id == tenancyId);
                    if (tenancy != null)
                    {
                        if (tenancy.Apartment!.Property!.LandlordId == userId)
                        {
                            canDelete = true;
                        }
                        if (!canDelete)
                        {
                            canDelete = await _context.PropertyManagerAssignments
                                .AnyAsync(m => m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                        }
                        if (!canDelete)
                        {
                            canDelete = await _context.ApartmentOwners
                                .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                        }
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



