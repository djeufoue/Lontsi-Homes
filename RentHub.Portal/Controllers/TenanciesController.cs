using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using RentHub.Portal.Helpers;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Tenancies;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class TenanciesController : Controller
    {
        private const int FinalCreateStep = 5;
        private static readonly JsonSerializerOptions SessionJsonOptions = new(JsonSerializerDefaults.Web);

        private readonly RentHubApiClient _api;
        private readonly ILogger<TenanciesController> _logger;

        public TenanciesController(RentHubApiClient api, ILogger<TenanciesController> logger)
        {
            _api = api;
            _logger = logger;
        }

        public async Task<IActionResult> Index(string? search = null)
        {
            try
            {
                var items = await _api.GetAsync<List<TenancyDto>>("tenancies");

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var s = search.Trim().ToLowerInvariant();
                    items = items.Where(t =>
                        (t.PropertyName ?? string.Empty).ToLowerInvariant().Contains(s) ||
                        (t.ApartmentName ?? string.Empty).ToLowerInvariant().Contains(s))
                        .ToList();
                }

                return View(new TenancyIndexVm
                {
                    Search = search,
                    Items = items
                });
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction("Index", "Properties"));
            }
        }

        public async Task<IActionResult> Overview(int id, string? memberSearch = null)
        {
            try
            {
                var vm = await BuildOverviewVmAsync(id, memberSearch);
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, vm.Tenancy.PropertyId);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction("Index", "Properties"));
            }
        }

        [HttpGet]
        public async Task<IActionResult> Create(int apartmentId, int step = 1)
        {
            try
            {
                var draft = await LoadOrCreateDraftAsync(apartmentId);
                step = Math.Clamp(step, 1, FinalCreateStep);
                return View(BuildCreateVm(draft, step));
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction("Overview", "Apartments", new { id = apartmentId }));
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDetails(TenancyDetailsStepVm vm)
        {
            try
            {
                var draft = await LoadOrCreateDraftAsync(vm.ApartmentId);
                var errors = new List<string>();

                if (!ModelState.IsValid)
                {
                    errors.Add("Please complete the tenancy details before continuing.");
                }

                if (vm.ContractDocument != null && vm.ContractDocument.Length > 0 && !IsPdf(vm.ContractDocument))
                {
                    errors.Add("The tenancy contract must be a PDF file.");
                }

                if (vm.MonthlyRent <= 0)
                {
                    errors.Add("Monthly rent must be greater than zero.");
                }

                if (vm.MaxMembers < 1)
                {
                    errors.Add("Max members must be at least 1.");
                }

                if (vm.RentDueDay is < 1 or > 31)
                {
                    errors.Add("Rent due day must be between 1 and 31.");
                }

                if (vm.EndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !vm.EndDate.HasValue)
                {
                    errors.Add("Please provide an end date when the tenancy expires automatically.");
                }

                if (vm.EndDate.HasValue && vm.EndDate.Value.Date < vm.StartDate.Date)
                {
                    errors.Add("End date cannot be before the start date.");
                }

                if (errors.Any())
                {
                    return View("Create", BuildCreateVm(draft, 1, errors));
                }

                DeleteDraftContract(draft);

                draft.StartDate = vm.StartDate;
                draft.EndBehavior = vm.EndBehavior;
                draft.EndDate = vm.EndBehavior == TenancyEndBehaviorEnum.NoEndDate ? null : vm.EndDate;
                draft.MonthlyRent = vm.MonthlyRent;
                draft.MaxMembers = vm.MaxMembers;
                draft.RentDueDay = vm.RentDueDay;

                if (vm.ContractDocument != null && vm.ContractDocument.Length > 0)
                {
                    await SaveDraftContractAsync(draft, vm.ContractDocument);
                }

                RegenerateRentPeriods(draft);
                SaveDraft(draft);

                return RedirectToAction(nameof(Create), new { apartmentId = vm.ApartmentId, step = 2 });
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction(nameof(Create), new { apartmentId = vm.ApartmentId, step = 1 }));
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveImport(TenancyImportStepVm vm)
        {
            try
            {
                var draft = await LoadOrCreateDraftAsync(vm.ApartmentId);

                draft.ImportMode = vm.ImportMode;
                draft.UnpaidFrom = vm.UnpaidFrom;
                draft.UnpaidTo = vm.UnpaidTo;
                draft.PaidInAdvanceFrom = vm.PaidInAdvanceFrom;
                draft.PaidInAdvanceTo = vm.PaidInAdvanceTo;

                var errors = ValidateImport(draft);
                if (errors.Any())
                {
                    RegenerateRentPeriods(draft);
                    return View("Create", BuildCreateVm(draft, 3, errors));
                }

                RegenerateRentPeriods(draft);
                SaveDraft(draft);

                return RedirectToAction(nameof(Create), new { apartmentId = vm.ApartmentId, step = 4 });
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction(nameof(Create), new { apartmentId = vm.ApartmentId, step = 3 }));
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTenant(TenancyTenantStepVm vm)
        {
            try
            {
                var draft = await LoadOrCreateDraftAsync(vm.ApartmentId);
                var errors = new List<string>();

                if (!ModelState.IsValid)
                {
                    errors.Add("Please provide at least a valid tenant email before continuing.");
                }

                if (vm.Role != TenancyMemberRoleEnum.MainTenant)
                {
                    errors.Add("The guided flow must invite a main tenant first.");
                }

                if (errors.Any())
                {
                    return View("Create", BuildCreateVm(draft, 4, errors));
                }

                draft.MainTenant = new TenantInvitationRequest
                {
                    Email = vm.Email.Trim(),
                    FullName = vm.FullName?.Trim(),
                    CountryCode = vm.CountryCode?.Trim(),
                    PhoneNumber = vm.PhoneNumber?.Trim(),
                    WhatsAppPhoneNumber = vm.WhatsAppPhoneNumber?.Trim(),
                    Role = TenancyMemberRoleEnum.MainTenant
                };

                SaveDraft(draft);

                return RedirectToAction(nameof(Create), new { apartmentId = vm.ApartmentId, step = FinalCreateStep });
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction(nameof(Create), new { apartmentId = vm.ApartmentId, step = 4 }));
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteCreate(int apartmentId)
        {
            var draft = LoadDraft(apartmentId);
            if (draft == null)
            {
                TempData["Error"] = "The tenancy draft expired. Please start again.";
                return RedirectToAction("Overview", "Apartments", new { id = apartmentId });
            }

            var errors = ValidateDraftForSubmit(draft);
            if (errors.Any())
            {
                return View("Create", BuildCreateVm(draft, FinalCreateStep, errors));
            }

            try
            {
                var request = new CreateGuidedTenancyRequest
                {
                    ApartmentId = draft.ApartmentId,
                    StartDate = draft.StartDate,
                    EndDate = draft.EndDate,
                    MonthlyRent = draft.MonthlyRent,
                    MaxMembers = draft.MaxMembers,
                    RentDueDay = draft.RentDueDay,
                    EndBehavior = draft.EndBehavior,
                    RentPeriods = draft.RentPeriods,
                    MainTenant = draft.MainTenant
                };

                var tenancy = await _api.PostAsync<CreateGuidedTenancyRequest, TenancyDto>("tenancies/guided", request);

                if (!string.IsNullOrWhiteSpace(draft.ContractTempPath) && System.IO.File.Exists(draft.ContractTempPath))
                {
                    await using var stream = System.IO.File.OpenRead(draft.ContractTempPath);
                    var content = new MultipartFormDataContent();
                    content.Add(new StringContent(DocumentTypeEnum.TenancyContract.ToString()), "DocumentType");
                    content.Add(new StreamContent(stream), "File", draft.ContractFileName ?? "tenancy-contract.pdf");
                    await _api.PostMultipartAsync<DocumentDto>($"documents/tenancy/{tenancy.Id}", content);
                }

                DeleteDraftContract(draft);
                ClearDraft(apartmentId);

                TempData["Success"] = "Tenancy created. The tenant invitation was sent by email.";
                return RedirectToAction(nameof(Overview), new { id = tenancy.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Guided tenancy creation failed in portal for apartment {ApartmentId}.", apartmentId);
                var apiError = ParseApiError(ex.Message);
                var message = SafeUserMessage(apiError.Message, "Unable to create the tenancy right now. Please review the setup and try again.");
                return View("Create", BuildCreateVm(draft, FinalCreateStep, new List<string> { message }));
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult CancelCreate(int apartmentId)
        {
            var draft = LoadDraft(apartmentId);
            if (draft != null)
            {
                DeleteDraftContract(draft);
            }

            ClearDraft(apartmentId);
            TempData["Info"] = "Tenancy creation was cancelled.";
            return RedirectToAction("Overview", "Apartments", new { id = apartmentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMember(int tenancyId, AddTenancyMemberRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    TempData["Error"] = "Please complete the tenancy member details before saving.";
                    return RedirectToAction(nameof(Overview), new { id = tenancyId });
                }

                await _api.PostAsync($"tenancies/{tenancyId}/members", request);
                TempData["Success"] = "Tenancy member added.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Add tenancy member request failed in portal for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to add the tenancy member right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMemberRole(int tenancyId, int memberId, UpdateTenancyMemberRequest request)
        {
            try
            {
                await _api.PutAsync($"tenancies/{tenancyId}/members/{memberId}", request);
                TempData["Success"] = "Tenancy member updated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update tenancy member failed in portal for tenancy {TenancyId} member {MemberId}.", tenancyId, memberId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update the tenancy member right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveMember(int tenancyId, int memberId)
        {
            try
            {
                await _api.DeleteAsync($"tenancies/{tenancyId}/members/{memberId}");
                TempData["Success"] = "Tenancy member removed.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remove tenancy member failed in portal for tenancy {TenancyId} member {MemberId}.", tenancyId, memberId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to remove the tenancy member right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadContract(int tenancyId, IFormFile file, int? currentDocumentId = null)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose a tenancy contract PDF to upload.";
                return RedirectToAction(nameof(Overview), new { id = tenancyId });
            }

            if (!IsPdf(file))
            {
                TempData["Error"] = "Only PDF files are allowed for the tenancy contract.";
                return RedirectToAction(nameof(Overview), new { id = tenancyId });
            }

            try
            {
                var existingOverview = await _api.GetAsync<TenancyOverviewDto>($"tenancies/{tenancyId}/overview");
                var existingContractIds = existingOverview.Documents
                    .Where(doc => doc.DocumentType == DocumentTypeEnum.TenancyContract)
                    .Select(doc => doc.Id)
                    .ToList();

                var content = new MultipartFormDataContent();
                content.Add(new StringContent(DocumentTypeEnum.TenancyContract.ToString()), "DocumentType");
                content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

                var createdDocument = await _api.PostMultipartAsync<DocumentDto>($"documents/tenancy/{tenancyId}", content);

                foreach (var documentId in existingContractIds.Where(id => !currentDocumentId.HasValue || id != createdDocument.Id))
                {
                    try
                    {
                        await _api.DeleteAsync($"documents/{documentId}");
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogWarning(deleteEx, "Tenancy contract cleanup failed in portal for tenancy {TenancyId} document {DocumentId}.", tenancyId, documentId);
                    }
                }

                TempData["Success"] = existingContractIds.Any() ? "Tenancy contract updated." : "Tenancy contract uploaded.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload tenancy contract request failed in portal for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to upload the tenancy contract right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocument(int tenancyId, int documentId)
        {
            try
            {
                await _api.DeleteAsync($"documents/{documentId}");
                TempData["Success"] = "Tenancy contract deleted.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete tenancy contract failed in portal for tenancy {TenancyId} document {DocumentId}.", tenancyId, documentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to delete the tenancy contract right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Terminate(
            int tenancyId,
            DateTimeOffset terminationDate,
            TenancyTerminationReasonEnum reason,
            FutureRentHandlingEnum futureRentHandling,
            string? notes,
            List<int>? waivedRentPeriodIds)
        {
            try
            {
                if (terminationDate == default)
                {
                    TempData["Error"] = "Please provide a termination date.";
                    return RedirectToAction(nameof(Overview), new { id = tenancyId });
                }

                await _api.PostAsync($"tenancies/{tenancyId}/terminate", new TerminateTenancyRequest
                {
                    TerminationDate = terminationDate,
                    Reason = reason,
                    FutureRentHandling = futureRentHandling,
                    Notes = notes,
                    WaivedRentPeriodIds = waivedRentPeriodIds ?? new List<int>()
                });

                TempData["Success"] = "Tenancy was terminated. Existing unpaid rent remains payable unless it was waived.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Terminate tenancy failed in portal for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to terminate the tenancy right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        private async Task<TenancyCreateDraft> LoadOrCreateDraftAsync(int apartmentId)
        {
            var existing = LoadDraft(apartmentId);
            if (existing != null)
            {
                RegenerateRentPeriods(existing);
                return existing;
            }

            var overview = await _api.GetAsync<ApartmentOverviewDto>($"apartments/{apartmentId}/overview");
            var apartment = overview.Apartment;
            var today = DateTimeOffset.Now.Date;
            var draft = new TenancyCreateDraft
            {
                ApartmentId = apartment.Id,
                ApartmentName = apartment.Name,
                PropertyId = apartment.PropertyId,
                PropertyName = apartment.PropertyName,
                StartDate = today,
                MonthlyRent = apartment.Price > 0 ? apartment.Price : 1,
                MaxMembers = 1,
                RentDueDay = 1,
                EndBehavior = TenancyEndBehaviorEnum.NoEndDate,
                ImportMode = RentPaymentImportModeEnum.AllGeneratedPeriodsUnpaid
            };

            RegenerateRentPeriods(draft);
            SaveDraft(draft);
            return draft;
        }

        private TenancyCreateWizardVm BuildCreateVm(TenancyCreateDraft draft, int step, List<string>? errors = null)
        {
            return new TenancyCreateWizardVm
            {
                Step = Math.Clamp(step, 1, FinalCreateStep),
                Draft = draft,
                ValidationErrors = errors ?? new List<string>()
            };
        }

        private TenancyCreateDraft? LoadDraft(int apartmentId)
        {
            var raw = HttpContext.Session.GetString(SessionKey(apartmentId));
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<TenancyCreateDraft>(raw, SessionJsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Tenancy draft session payload could not be deserialized for apartment {ApartmentId}.", apartmentId);
                ClearDraft(apartmentId);
                return null;
            }
        }

        private void SaveDraft(TenancyCreateDraft draft)
        {
            HttpContext.Session.SetString(SessionKey(draft.ApartmentId), JsonSerializer.Serialize(draft, SessionJsonOptions));
        }

        private void ClearDraft(int apartmentId)
        {
            HttpContext.Session.Remove(SessionKey(apartmentId));
        }

        private static string SessionKey(int apartmentId) => $"TenancyCreateDraft:{apartmentId}";

        private void RegenerateRentPeriods(TenancyCreateDraft draft)
        {
            draft.RentPeriods = RentPeriodScheduleHelper.GeneratePeriods(
                draft.StartDate,
                draft.EndDate,
                draft.EndBehavior,
                draft.MonthlyRent,
                draft.RentDueDay,
                DateTimeOffset.UtcNow);

            RentPeriodScheduleHelper.ApplyImportMode(
                draft.RentPeriods,
                draft.ImportMode,
                DateTimeOffset.UtcNow,
                draft.UnpaidFrom,
                draft.UnpaidTo,
                draft.PaidInAdvanceFrom,
                draft.PaidInAdvanceTo);
        }

        private List<string> ValidateImport(TenancyCreateDraft draft)
        {
            RegenerateRentPeriods(draft);
            var errors = new List<string>();

            if (!draft.RentPeriods.Any())
            {
                errors.Add("Please complete tenancy details first so rent periods can be generated.");
                return errors;
            }

            if (draft.ImportMode == RentPaymentImportModeEnum.SomePeriodsWerePaid)
            {
                if (!draft.UnpaidFrom.HasValue || !draft.UnpaidTo.HasValue)
                {
                    errors.Add("Please provide the continuous unpaid date range.");
                }
                else
                {
                    errors.AddRange(RentPeriodScheduleHelper.ValidateContiguousRange(
                        draft.RentPeriods,
                        draft.UnpaidFrom.Value,
                        draft.UnpaidTo.Value,
                        "Unpaid"));
                }
            }

            if (draft.ImportMode == RentPaymentImportModeEnum.TenantPaidInAdvance ||
                draft.PaidInAdvanceFrom.HasValue ||
                draft.PaidInAdvanceTo.HasValue)
            {
                if (!draft.PaidInAdvanceFrom.HasValue || !draft.PaidInAdvanceTo.HasValue)
                {
                    errors.Add("Please provide both prepaid start and prepaid end dates.");
                }
                else
                {
                    errors.AddRange(RentPeriodScheduleHelper.ValidateContiguousRange(
                        draft.RentPeriods,
                        draft.PaidInAdvanceFrom.Value,
                        draft.PaidInAdvanceTo.Value,
                        "Paid in advance"));
                }
            }

            return errors;
        }

        private List<string> ValidateDraftForSubmit(TenancyCreateDraft draft)
        {
            var errors = new List<string>();

            if (draft.ApartmentId <= 0)
            {
                errors.Add("Apartment is missing from the tenancy draft.");
            }

            if (draft.MonthlyRent <= 0)
            {
                errors.Add("Monthly rent must be greater than zero.");
            }

            if (draft.MaxMembers < 1)
            {
                errors.Add("Max members must be at least 1.");
            }

            if (draft.RentDueDay is < 1 or > 31)
            {
                errors.Add("Rent due day must be between 1 and 31.");
            }

            if (draft.EndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !draft.EndDate.HasValue)
            {
                errors.Add("An automatically expiring tenancy requires an end date.");
            }

            if (string.IsNullOrWhiteSpace(draft.MainTenant.Email))
            {
                errors.Add("Please invite the main tenant before creating the tenancy.");
            }

            errors.AddRange(ValidateImport(draft));

            return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task SaveDraftContractAsync(TenancyCreateDraft draft, IFormFile file)
        {
            var draftRoot = Path.Combine(Path.GetTempPath(), "RentHubTenancyDrafts");
            Directory.CreateDirectory(draftRoot);

            var fileName = $"{Guid.NewGuid():N}.pdf";
            var tempPath = Path.Combine(draftRoot, fileName);

            await using (var output = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(output);
            }

            draft.ContractTempPath = tempPath;
            draft.ContractFileName = file.FileName;
            draft.ContractContentType = file.ContentType;
        }

        private void DeleteDraftContract(TenancyCreateDraft draft)
        {
            if (string.IsNullOrWhiteSpace(draft.ContractTempPath))
            {
                return;
            }

            try
            {
                if (System.IO.File.Exists(draft.ContractTempPath))
                {
                    System.IO.File.Delete(draft.ContractTempPath);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Unable to delete tenancy draft contract {ContractTempPath}.", draft.ContractTempPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unable to delete tenancy draft contract {ContractTempPath}.", draft.ContractTempPath);
            }

            draft.ContractTempPath = null;
            draft.ContractFileName = null;
            draft.ContractContentType = null;
        }

        private async Task<TenancyOverviewVm> BuildOverviewVmAsync(int id, string? memberSearch)
        {
            var overview = await _api.GetAsync<TenancyOverviewDto>($"tenancies/{id}/overview");
            var members = overview.Members ?? new List<TenancyMemberDto>();
            if (!string.IsNullOrWhiteSpace(memberSearch))
            {
                var search = memberSearch.Trim().ToLowerInvariant();
                members = members
                    .Where(m => (m.FullName ?? string.Empty).ToLowerInvariant().Contains(search)
                             || (m.Email ?? string.Empty).ToLowerInvariant().Contains(search)
                             || (m.Role ?? string.Empty).ToLowerInvariant().Contains(search))
                    .ToList();
            }

            return new TenancyOverviewVm
            {
                Tenancy = overview.Tenancy,
                Members = members,
                Documents = (overview.Documents ?? new List<DocumentDto>())
                    .Where(doc => doc.DocumentType == DocumentTypeEnum.TenancyContract)
                    .OrderByDescending(doc => doc.UploadedAt)
                    .ToList(),
                RentPeriods = (overview.RentPeriods ?? new List<RentPeriodDto>())
                    .OrderBy(period => period.PeriodStart)
                    .ToList(),
                MemberSearch = memberSearch
            };
        }

        private Task<IActionResult> HandleApiFailureAsync(Exception ex, IActionResult? fallback = null)
        {
            _logger.LogError(ex, "Tenancy request failed in portal.");

            var apiError = ParseApiError(ex.Message);
            TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to load the tenancy workspace right now. Please try again.");
            return Task.FromResult<IActionResult>(fallback ?? RedirectToAction("Index", "Properties"));
        }

        private static bool IsPdf(IFormFile file)
        {
            var contentType = file.ContentType ?? string.Empty;
            var extension = Path.GetExtension(file.FileName ?? string.Empty);

            return string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static ApiErrorPayload ParseApiError(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new ApiErrorPayload();

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.String)
                {
                    return new ApiErrorPayload { Message = root.GetString() };
                }

                string? ReadString(string key)
                {
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty(key, out var value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString();
                    }

                    return null;
                }

                return new ApiErrorPayload
                {
                    Code = ReadString("Code") ?? ReadString("code"),
                    Message = ReadString("Message") ?? ReadString("message")
                };
            }
            catch
            {
                return new ApiErrorPayload { Message = raw };
            }
        }

        private static string SafeUserMessage(string? apiMessage, string fallback)
        {
            if (string.IsNullOrWhiteSpace(apiMessage))
            {
                return fallback;
            }

            return LooksTechnicalMessage(apiMessage) ? fallback : apiMessage;
        }

        private static bool LooksTechnicalMessage(string message)
        {
            var normalized = message.Trim();
            if (normalized.Length == 0)
            {
                return true;
            }

            var technicalFragments = new[]
            {
                "exception",
                "stack trace",
                "inner exception",
                "dbupdateexception",
                "sqlexception",
                "invalid column name",
                "entity changes",
                "microsoft.entityframeworkcore",
                " at "
            };

            return technicalFragments.Any(fragment =>
                normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class ApiErrorPayload
        {
            public string? Code { get; set; }
            public string? Message { get; set; }
        }
    }
}
