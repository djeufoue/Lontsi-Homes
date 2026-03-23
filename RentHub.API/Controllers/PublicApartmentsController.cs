using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Storage;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/public/apartments")]
    public class PublicApartmentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IStorageService _storageService;

        public PublicApartmentsController(ApplicationDbContext context, IStorageService storageService)
        {
            _context = context;
            _storageService = storageService;
        }

        [HttpGet]
        public async Task<IActionResult> GetPublicApartments(
            [FromQuery] string? search = null,
            [FromQuery] string? city = null,
            [FromQuery] string? status = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 12)
        {
            try
            {
                page = page < 1 ? 1 : page;
                pageSize = pageSize < 1 ? 12 : Math.Min(pageSize, 50);

                var query = _context.Apartments
                    .Include(a => a.Property)
                    .ThenInclude(p => p!.Landlord)
                    .Where(a => !a.IsDeleted && a.Property != null && !a.Property.IsDeleted)
                    .AsQueryable();

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var normalized = search.Trim().ToLowerInvariant();
                    query = query.Where(a =>
                        a.Name.ToLower().Contains(normalized) ||
                        a.Property!.Name.ToLower().Contains(normalized) ||
                        a.Property.City.ToLower().Contains(normalized) ||
                        a.Property.Address.ToLower().Contains(normalized));
                }

                if (!string.IsNullOrWhiteSpace(city))
                {
                    var normalized = city.Trim().ToLowerInvariant();
                    query = query.Where(a => a.Property!.City.ToLower().Contains(normalized));
                }

                if (!string.IsNullOrWhiteSpace(status) &&
                    !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse<ApartmentStatusEnum>(status, true, out var parsedStatus))
                {
                    query = query.Where(a => a.Status == parsedStatus);
                }

                var totalCount = await query.CountAsync();
                var apartments = await query
                    .OrderBy(a => a.Status == ApartmentStatusEnum.Vacant ? 0 : 1)
                    .ThenByDescending(a => a.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var leadImageMap = await BuildLeadImageMapAsync(apartments);

                var response = new PublicApartmentCatalogResponseDto
                {
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = totalCount,
                    Items = apartments.Select(apartment => new PublicApartmentCatalogItemDto
                    {
                        ApartmentId = apartment.Id,
                        PropertyId = apartment.PropertyId,
                        ApartmentName = apartment.Name,
                        PropertyName = apartment.Property!.Name,
                        City = apartment.Property.City,
                        Address = apartment.Property.Address,
                        Type = apartment.Type.ToString(),
                        Status = apartment.Status.ToString(),
                        Price = apartment.Price,
                        DepositPrice = apartment.DepositPrice,
                        Area = apartment.Area,
                        NumberOfRooms = apartment.NumberOfRooms,
                        NumberOfBathrooms = apartment.NumberOfBathrooms,
                        FloorNumber = apartment.FloorNumber,
                        LandlordName = apartment.Property.Landlord != null
                            ? (apartment.Property.Landlord.FullName ?? apartment.Property.Landlord.Email ?? "Landlord")
                            : "Landlord",
                        LeadImageUrl = leadImageMap.TryGetValue(apartment.Id, out var imageUrl) ? imageUrl : null
                    }).ToList()
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetPublicApartment(int id)
        {
            try
            {
                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .ThenInclude(p => p!.Landlord)
                    .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted);

                if (apartment == null || apartment.Property == null || apartment.Property.IsDeleted)
                {
                    return NotFound("Apartment not found.");
                }

                var propertyImageEntities = await _context.Documents
                    .Where(d => d.PropertyId == apartment.PropertyId &&
                                d.ApartmentId == null &&
                                d.DocumentType == DocumentTypeEnum.PropertyImage)
                    .OrderByDescending(d => d.CreatedAt)
                    .ToListAsync();

                var apartmentImageEntities = await _context.Documents
                    .Where(d => d.ApartmentId == apartment.Id &&
                                d.DocumentType == DocumentTypeEnum.ApartmentImage)
                    .OrderByDescending(d => d.CreatedAt)
                    .ToListAsync();

                var dto = new PublicApartmentOverviewDto
                {
                    ApartmentId = apartment.Id,
                    PropertyId = apartment.PropertyId,
                    ApartmentName = apartment.Name,
                    PropertyName = apartment.Property.Name,
                    PropertyDescription = apartment.Property.Description ?? string.Empty,
                    City = apartment.Property.City,
                    Address = apartment.Property.Address,
                    Type = apartment.Type.ToString(),
                    Status = apartment.Status.ToString(),
                    Price = apartment.Price,
                    DepositPrice = apartment.DepositPrice,
                    Area = apartment.Area,
                    NumberOfRooms = apartment.NumberOfRooms,
                    NumberOfBathrooms = apartment.NumberOfBathrooms,
                    FloorNumber = apartment.FloorNumber,
                    LandlordName = apartment.Property.Landlord != null
                        ? (apartment.Property.Landlord.FullName ?? apartment.Property.Landlord.Email ?? "Landlord")
                        : "Landlord",
                    PropertyImages = await ToMediaItemsAsync(propertyImageEntities, "Property image"),
                    ApartmentImages = await ToMediaItemsAsync(apartmentImageEntities, "Apartment image")
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private async Task<Dictionary<int, string>> BuildLeadImageMapAsync(IEnumerable<Apartment> apartments)
        {
            var apartmentIds = apartments.Select(a => a.Id).Distinct().ToList();
            var propertyIds = apartments.Select(a => a.PropertyId).Distinct().ToList();

            var apartmentImages = await _context.Documents
                .Where(d => d.ApartmentId != null &&
                            apartmentIds.Contains(d.ApartmentId.Value) &&
                            d.DocumentType == DocumentTypeEnum.ApartmentImage)
                .GroupBy(d => d.ApartmentId!.Value)
                .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
                .ToListAsync();

            var propertyImages = await _context.Documents
                .Where(d => d.PropertyId != null &&
                            propertyIds.Contains(d.PropertyId.Value) &&
                            d.ApartmentId == null &&
                            d.DocumentType == DocumentTypeEnum.PropertyImage)
                .GroupBy(d => d.PropertyId!.Value)
                .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
                .ToListAsync();

            var apartmentImageUrls = new Dictionary<int, string>();
            foreach (var image in apartmentImages)
            {
                if (image.ApartmentId.HasValue)
                {
                    apartmentImageUrls[image.ApartmentId.Value] = await _storageService.GetReadUrlAsync(image.BlobUrl);
                }
            }

            var propertyImageUrls = new Dictionary<int, string>();
            foreach (var image in propertyImages)
            {
                if (image.PropertyId.HasValue)
                {
                    propertyImageUrls[image.PropertyId.Value] = await _storageService.GetReadUrlAsync(image.BlobUrl);
                }
            }

            var result = new Dictionary<int, string>();
            foreach (var apartment in apartments)
            {
                if (apartmentImageUrls.TryGetValue(apartment.Id, out var apartmentUrl))
                {
                    result[apartment.Id] = apartmentUrl;
                    continue;
                }

                if (propertyImageUrls.TryGetValue(apartment.PropertyId, out var propertyUrl))
                {
                    result[apartment.Id] = propertyUrl;
                }
            }

            return result;
        }

        private async Task<List<PublicMediaItemDto>> ToMediaItemsAsync(IEnumerable<Document> documents, string labelPrefix)
        {
            var items = new List<PublicMediaItemDto>();
            var index = 1;
            foreach (var document in documents)
            {
                items.Add(new PublicMediaItemDto
                {
                    DocumentId = document.Id,
                    Url = await _storageService.GetReadUrlAsync(document.BlobUrl),
                    Label = $"{labelPrefix} {index++}"
                });
            }

            return items;
        }
    }
}
