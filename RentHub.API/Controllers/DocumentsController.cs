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

        /// <summary>
        /// Uploads a document associated with a property.  Only the landlord of the property
        /// or a manager with write permission may upload files for the property.
        /// The document type must be provided to indicate how the file should be used.
        /// </summary>
        [HttpPost("property/{propertyId}")]
        [Authorize]
        public async Task<IActionResult> UploadForProperty([FromRoute] int propertyId, [FromForm] UploadDocumentRequest request)
        {
            try
            {
                if (request.File == null || request.File.Length == 0) return BadRequest("File is required.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                if (property == null) return NotFound("Property not found.");
                // Check permissions: landlord or manager with write
                bool canWrite = false;
                if (property.LandlordId == userId)
                {
                    canWrite = true;
                }
                else
                {
                    canWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                }
                if (!canWrite) return Forbid();
                // Upload file to storage
                var extension = Path.GetExtension(request.File.FileName);
                var blobName = $"property-{propertyId}-{Guid.NewGuid()}{extension}";
                string blobUrl;
                using (var stream = request.File.OpenReadStream())
                {
                    blobUrl = await _storageService.UploadFileAsync(stream, blobName, request.File.ContentType);
                }
                // Create document record
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
                var dto = new DocumentDto
                {
                    Id = document.Id,
                    FileName = document.FileName,
                    BlobUrl = document.BlobUrl,
                    DocumentType = document.DocumentType,
                    UploadedAt = document.UploadedAt,
                    PropertyId = document.PropertyId
                };
                return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Uploads a document associated with an apartment.  Only the landlord, a manager
        /// with write permission or an owner with write permission may upload files for
        /// the apartment.
        /// </summary>
        [HttpPost("apartment/{apartmentId}")]
        [Authorize]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadForApartment(
            int apartmentId,
            [FromForm] UploadApartmentDocumentRequest request)
        {
            try
            {
                if (request?.File == null || request.File.Length == 0)
                    return BadRequest("File is required.");

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId);

                if (apartment == null)
                    return NotFound("Apartment not found.");

                // Check permissions: landlord, manager write, or owner write
                bool canWrite = false;
                if (apartment.Property?.LandlordId == userId)
                {
                    canWrite = true;
                }
                else
                {
                    // manager write on property
                    var managerWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == apartment.PropertyId &&
                                       m.ManagerId == userId &&
                                       m.Permission == PermissionLevelEnum.ReadWrite);

                    // owner write on apartment
                    var ownerWrite = await _context.ApartmentOwners
                        .AnyAsync(o => o.ApartmentId == apartmentId &&
                                       o.OwnerId == userId &&
                                       o.Permission == PermissionLevelEnum.ReadWrite);

                    canWrite = managerWrite || ownerWrite;
                }

                if (!canWrite)
                    return Forbid();

                // Upload file
                var file = request.File;
                var documentType = request.DocumentType;

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
                    DocumentType = documentType,
                    ApartmentId = apartmentId,
                    PropertyId = apartment.PropertyId,
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };

                _context.Documents.Add(document);
                await _context.SaveChangesAsync();

                var dto = new DocumentDto
                {
                    Id = document.Id,
                    FileName = document.FileName,
                    BlobUrl = document.BlobUrl,
                    DocumentType = document.DocumentType,
                    UploadedAt = document.UploadedAt,
                    PropertyId = document.PropertyId,
                    ApartmentId = document.ApartmentId
                };

                return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Uploads a document associated with a tenancy.  Only the landlord, a manager
        /// with write permission or an owner with write permission may upload files for
        /// the tenancy.
        /// </summary>
        [HttpPost("tenancy/{tenancyId}")]
        [Authorize]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadForTenancy(
            int tenancyId,
            [FromForm] UploadTenancyDocumentRequest request)
        {
            try
            {
                if (request?.File == null || request.File.Length == 0)
                    return BadRequest("File is required.");

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

                // Check permissions: landlord, manager write, owner write
                bool canWrite = false;
                if (apartment.Property?.LandlordId == userId)
                {
                    canWrite = true;
                }
                else
                {
                    var managerWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == apartment.PropertyId &&
                                       m.ManagerId == userId &&
                                       m.Permission == PermissionLevelEnum.ReadWrite);

                    var ownerWrite = await _context.ApartmentOwners
                        .AnyAsync(o => o.ApartmentId == apartment.Id &&
                                       o.OwnerId == userId &&
                                       o.Permission == PermissionLevelEnum.ReadWrite);

                    canWrite = managerWrite || ownerWrite;
                }

                if (!canWrite)
                    return Forbid();

                // Upload file
                var file = request.File;
                var documentType = request.DocumentType;

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
                    DocumentType = documentType,
                    TenancyId = tenancyId,
                    ApartmentId = apartment.Id,
                    PropertyId = apartment.PropertyId,
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };

                _context.Documents.Add(document);
                await _context.SaveChangesAsync();

                var dto = new DocumentDto
                {
                    Id = document.Id,
                    FileName = document.FileName,
                    BlobUrl = document.BlobUrl,
                    DocumentType = document.DocumentType,
                    UploadedAt = document.UploadedAt,
                    PropertyId = document.PropertyId,
                    ApartmentId = document.ApartmentId,
                    TenancyId = document.TenancyId
                };

                return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Retrieves metadata about a single document.  Access is restricted to the
        /// landlord, managers, owners or tenant associated with the document's entity.
        /// </summary>
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
                // Check access: uploader, landlord, manager, owner or tenant associated with the doc
                bool hasAccess = document.UserId == userId;
                // Property level access
                if (document.PropertyId.HasValue)
                {
                    var propertyId = document.PropertyId.Value;
                    var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                    if (property != null)
                    {
                        // landlord
                        if (property.LandlordId == userId) hasAccess = true;
                        // manager on property
                        if (!hasAccess)
                        {
                            hasAccess = await _context.PropertyManagerAssignments
                                .AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId);
                        }
                        // owner of any apartment in property
                        if (!hasAccess)
                        {
                            hasAccess = await _context.ApartmentOwners
                                .Include(o => o.Apartment)
                                .AnyAsync(o => o.Apartment!.PropertyId == propertyId && o.OwnerId == userId);
                        }
                    }
                }
                // Apartment level access
                if (!hasAccess && document.ApartmentId.HasValue)
                {
                    var apartmentId = document.ApartmentId.Value;
                    var apartment = await _context.Apartments
                        .Include(a => a.Property)
                        .FirstOrDefaultAsync(a => a.Id == apartmentId);
                    if (apartment != null)
                    {
                        // landlord
                        if (apartment.Property?.LandlordId == userId) hasAccess = true;
                        // manager of property
                        if (!hasAccess)
                        {
                            hasAccess = await _context.PropertyManagerAssignments
                                .AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId);
                        }
                        // owner of apartment
                        if (!hasAccess)
                        {
                            hasAccess = await _context.ApartmentOwners
                                .AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId);
                        }
                        // tenant occupying apartment
                        if (!hasAccess)
                        {
                            hasAccess = await _context.Tenancies.AnyAsync(t => t.ApartmentId == apartmentId && t.TenantId == userId);
                        }
                    }
                }
                // Tenancy level access
                if (!hasAccess && document.TenancyId.HasValue)
                {
                    var tenancyId = document.TenancyId.Value;
                    var tenancy = await _context.Tenancies
                        .Include(t => t.Apartment!.Property)
                        .FirstOrDefaultAsync(t => t.Id == tenancyId);

                    if (tenancy != null)
                    {
                        // landlord
                        if (tenancy.Apartment!.Property!.LandlordId == userId) hasAccess = true;
                        // manager of property
                        if (!hasAccess)
                        {
                            hasAccess = await _context.PropertyManagerAssignments
                                .AnyAsync(m => m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId);
                        }
                        // owner of apartment
                        if (!hasAccess)
                        {
                            hasAccess = await _context.ApartmentOwners
                                .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId);
                        }
                        // tenant
                        if (!hasAccess && tenancy.TenantId == userId) hasAccess = true;
                    }
                }
                if (!hasAccess) return Forbid();
                var dto = new DocumentDto
                {
                    Id = document.Id,
                    FileName = document.FileName,
                    BlobUrl = document.BlobUrl,
                    DocumentType = document.DocumentType,
                    UploadedAt = document.UploadedAt,
                    PropertyId = document.PropertyId,
                    ApartmentId = document.ApartmentId,
                    TenancyId = document.TenancyId
                };
                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Lists all documents associated with a property.  Access is granted to the
        /// landlord, managers and owners associated with any apartment in the property.
        /// </summary>
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
                bool hasAccess = property.LandlordId == userId;
                if (!hasAccess)
                {
                    hasAccess = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId);
                    if (!hasAccess)
                    {
                        hasAccess = await _context.ApartmentOwners
                            .Include(o => o.Apartment)
                            .AnyAsync(o => o.Apartment!.PropertyId == propertyId && o.OwnerId == userId);
                    }
                }
                if (!hasAccess) return Forbid();
                var docs = await _context.Documents
                    .Where(d => d.PropertyId == propertyId)
                    .Select(d => new DocumentDto
                    {
                        Id = d.Id,
                        FileName = d.FileName,
                        BlobUrl = d.BlobUrl,
                        DocumentType = d.DocumentType,
                        UploadedAt = d.UploadedAt,
                        PropertyId = d.PropertyId,
                        ApartmentId = d.ApartmentId,
                        TenancyId = d.TenancyId
                    })
                    .ToListAsync();
                return Ok(docs);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Lists all documents associated with an apartment.  Access is granted to the
        /// landlord, managers, owners or the tenant occupying the apartment.
        /// </summary>
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
                bool hasAccess = apartment.Property?.LandlordId == userId;
                if (!hasAccess)
                {
                    hasAccess = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId);
                    if (!hasAccess)
                    {
                        hasAccess = await _context.ApartmentOwners
                            .AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId);
                        if (!hasAccess)
                        {
                            // tenant occupying
                            hasAccess = await _context.Tenancies.AnyAsync(t => t.ApartmentId == apartmentId && t.TenantId == userId);
                        }
                    }
                }
                if (!hasAccess) return Forbid();
                var docs = await _context.Documents
                    .Where(d => d.ApartmentId == apartmentId)
                    .Select(d => new DocumentDto
                    {
                        Id = d.Id,
                        FileName = d.FileName,
                        BlobUrl = d.BlobUrl,
                        DocumentType = d.DocumentType,
                        UploadedAt = d.UploadedAt,
                        PropertyId = d.PropertyId,
                        ApartmentId = d.ApartmentId,
                        TenancyId = d.TenancyId
                    })
                    .ToListAsync();
                return Ok(docs);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Lists all documents associated with a tenancy.  Access is granted to the landlord,
        /// managers, owners or the tenant involved in the tenancy.
        /// </summary>
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
                bool hasAccess = apartment.Property?.LandlordId == userId;
                if (!hasAccess)
                {
                    hasAccess = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId);
                    if (!hasAccess)
                    {
                        hasAccess = await _context.ApartmentOwners
                            .AnyAsync(o => o.ApartmentId == apartment.Id && o.OwnerId == userId);
                        if (!hasAccess)
                        {
                            hasAccess = tenancy.TenantId == userId;
                        }
                    }
                }
                if (!hasAccess) return Forbid();
                var docs = await _context.Documents
                    .Where(d => d.TenancyId == tenancyId)
                    .Select(d => new DocumentDto
                    {
                        Id = d.Id,
                        FileName = d.FileName,
                        BlobUrl = d.BlobUrl,
                        DocumentType = d.DocumentType,
                        UploadedAt = d.UploadedAt,
                        PropertyId = d.PropertyId,
                        ApartmentId = d.ApartmentId,
                        TenancyId = d.TenancyId
                    })
                    .ToListAsync();
                return Ok(docs);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Deletes a document.  Deletion is soft: the record is marked as deleted and the
        /// underlying file is removed from storage.  Only the uploader, landlord, manager
        /// with write permission or owner with write permission may delete documents.
        /// </summary>
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
                // Check property-level rights
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
                // Check apartment-level rights
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
                        // manager write
                        canDelete = await _context.PropertyManagerAssignments
                            .AnyAsync(m => m.PropertyId == apartment!.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    }
                    if (!canDelete)
                    {
                        // owner write
                        canDelete = await _context.ApartmentOwners
                            .AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    }
                }
                // Check tenancy-level rights
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
                            // manager write
                            canDelete = await _context.PropertyManagerAssignments
                                .AnyAsync(m => m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                        }
                        if (!canDelete)
                        {
                            // owner write
                            canDelete = await _context.ApartmentOwners
                                .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                        }
                    }
                }
                if (!canDelete) return Forbid();
                // Soft delete and remove blob
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
                    // If deleting file fails, ignore; we already soft-deleted the record.
                }
                return Ok(new { Message = "Document deleted." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}
