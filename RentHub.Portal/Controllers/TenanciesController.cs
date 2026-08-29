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
        private const string UnpaidRentStatusFilter = "Unpaid";
        private const string FutureRentStatusFilter = "Future";
        private const string AllRentStatusFilter = "All";
        private static readonly JsonSerializerOptions SessionJsonOptions = new(JsonSerializerDefaults.Web);

        private readonly RentHubApiClient _api;
        private readonly ILogger<TenanciesController> _logger;

        public TenanciesController(RentHubApiClient api, ILogger<TenanciesController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [Authorize(Roles = "Admin,Landlord,Manager,Tenant")]
        public async Task<IActionResult> Index(
            string? search = null,
            int? propertyId = null,
            string? status = null,
            int page = 1,
            int pageSize = 12)
        {
            try
            {
                var endpoint = $"workspace-directory/tenancies?search={Uri.EscapeDataString(search ?? string.Empty)}" +
                               $"&propertyId={propertyId}&status={Uri.EscapeDataString(status ?? string.Empty)}" +
                               $"&page={page}&pageSize={pageSize}";
                var model = await _api.GetAsync<WorkspaceDirectoryResponseDto<WorkspaceTenancyDto>>(endpoint);
                return View(model);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction("Index", "Properties"));
            }
        }

        public async Task<IActionResult> Overview(
            int id,
            string? memberSearch = null,
            string? rentStatus = null,
            DateTime? rentFrom = null,
            DateTime? rentTo = null,
            int rentPage = 1,
            int rentPageSize = 5)
        {
            try
            {
                var vm = await BuildOverviewVmAsync(id, memberSearch, rentStatus, rentFrom, rentTo, rentPage, rentPageSize);
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, vm.Tenancy.PropertyId);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction("Index", "Properties"));
            }
        }

        [HttpGet]
        [Authorize(Roles = "Landlord,Manager,Tenant")]
        public async Task<IActionResult> RentPeriods(
            int tenancyId,
            string? rentStatus = null,
            DateTime? rentFrom = null,
            DateTime? rentTo = null,
            string rentSortDirection = "priority",
            int rentPage = 1,
            int rentPageSize = 10,
            int rentGroupPage = 1)
        {
            try
            {
                var vm = await BuildOverviewVmAsync(
                    tenancyId,
                    null,
                    rentStatus,
                    rentFrom,
                    rentTo,
                    rentPage,
                    rentPageSize,
                    rentSortDirection,
                    applySmartDefaultRentStatus: true,
                    rentGroupPage: rentGroupPage);
                vm.IsRentPeriodsPage = true;
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, vm.Tenancy.PropertyId);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction(nameof(Index)));
            }
        }

        [HttpGet]
        public IActionResult Renewal(int tenancyId)
        {
            return RedirectToAction("Index", "TenancyRequests", new { tenancyId, requestType = "Renewal" });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestRenewal(TenancyRenewalVm vm)
        {
            var tenancyId = vm.Workspace.TenancyId;
            if (tenancyId <= 0) return RedirectToAction(nameof(Index));

            if (!vm.ProposedEndDate.HasValue)
            {
                ModelState.AddModelError(nameof(vm.ProposedEndDate), "Choose a proposed end date.");
            }

            if (!ModelState.IsValid)
            {
                try
                {
                    vm.Workspace = await _api.GetAsync<TenancyRenewalWorkspaceDto>($"tenancies/{tenancyId}/extension-requests");
                    return View(nameof(Renewal), vm);
                }
                catch (Exception ex)
                {
                    return await HandleApiFailureAsync(ex, RedirectToAction(nameof(Renewal), new { tenancyId }));
                }
            }

            try
            {
                await _api.PostAsync($"tenancies/{tenancyId}/extension-requests", new ExtendTenancyRequest
                {
                    NewEndDate = new DateTimeOffset(vm.ProposedEndDate!.Value.Date, TimeSpan.Zero)
                });
                TempData["Success"] = "Renewal request submitted.";
            }
            catch (Exception ex)
            {
                var error = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(error.Message, "Unable to submit the renewal request right now.");
            }

            return RedirectToAction("Index", "TenancyRequests", new { tenancyId, requestType = "Renewal" });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveRenewal(int tenancyId, int requestId)
        {
            if (tenancyId <= 0 || requestId <= 0) return RedirectToAction(nameof(Index));

            try
            {
                await _api.PutAsync($"tenancies/{tenancyId}/extension-requests/{requestId}/approve", new { });
                TempData["Success"] = "Renewal request approved.";
            }
            catch (Exception ex)
            {
                var error = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(error.Message, "Unable to approve the renewal request right now.");
            }

            return RedirectToAction("Index", "TenancyRequests", new { tenancyId, requestType = "Renewal" });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectRenewal(int tenancyId, int requestId, string? rejectionReason)
        {
            if (tenancyId <= 0 || requestId <= 0) return RedirectToAction(nameof(Index));
            if (string.IsNullOrWhiteSpace(rejectionReason))
            {
                TempData["Error"] = "A rejection reason is required.";
                return RedirectToAction("Index", "TenancyRequests", new { tenancyId, requestType = "Renewal" });
            }
            if (rejectionReason.Length > 512)
            {
                TempData["Error"] = "The rejection reason cannot exceed 512 characters.";
                return RedirectToAction("Index", "TenancyRequests", new { tenancyId, requestType = "Renewal" });
            }

            try
            {
                await _api.PutAsync($"tenancies/{tenancyId}/extension-requests/{requestId}/reject", new RejectTenancyExtensionRequest
                {
                    Reason = rejectionReason.Trim()
                });
                TempData["Success"] = "Renewal request rejected.";
            }
            catch (Exception ex)
            {
                var error = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(error.Message, "Unable to reject the renewal request right now.");
            }

            return RedirectToAction("Index", "TenancyRequests", new { tenancyId, requestType = "Renewal" });
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
                var normalizedEndBehavior = vm.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth
                    ? TenancyEndBehaviorEnum.NoEndDate
                    : vm.EndBehavior;

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

                if (vm.PaymentIntervalMonths is < 1 or > 12)
                {
                    errors.Add("Payment interval must be between 1 and 12 months.");
                }

                if (normalizedEndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !vm.EndDate.HasValue)
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
                draft.EndBehavior = normalizedEndBehavior;
                draft.EndDate = normalizedEndBehavior == TenancyEndBehaviorEnum.NoEndDate ? null : vm.EndDate;
                draft.PaymentIntervalMonths = vm.PaymentIntervalMonths;
                draft.MonthlyRent = vm.MonthlyRent;
                draft.MaxMembers = vm.MaxMembers;
                draft.RentDueDay = vm.RentDueDay;
                draft.RentTrackingStartDate = draft.StartDate;

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
                draft.HasSelectedImportMode = vm.ImportMode.HasValue;
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
                    CountryCode = Common.Helpers.PhoneNumberHelper.Normalize(vm.CountryCode),
                    PhoneNumber = Common.Helpers.PhoneNumberHelper.Normalize(vm.PhoneNumber),
                    WhatsAppPhoneNumber = Common.Helpers.PhoneNumberHelper.Normalize(vm.WhatsAppPhoneNumber),
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
                    PaymentIntervalMonths = draft.PaymentIntervalMonths,
                    EndBehavior = draft.EndBehavior,
                    RentTrackingStartDate = draft.RentTrackingStartDate,
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMemberEmail(int tenancyId, int propertyId, string memberId, string email)
        {
            try
            {
                await _api.PutAsync($"properties/{propertyId}/members/{Uri.EscapeDataString(memberId)}/email", new UpdateMemberEmailRequest
                {
                    Email = email
                });
                TempData["Success"] = "Email updated. The user was signed out and must complete verification again.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tenancy member email update failed for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update this email address.");
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
            string? notes)
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
                    Notes = notes
                });

                TempData["Success"] = "Tenancy termination decision recorded. Rent tracking will stop on the selected date.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Terminate tenancy failed in portal for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to terminate the tenancy right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkRentPeriodPaid(int tenancyId, int rentPeriodId, string? returnUrl = null)
        {
            try
            {
                if (rentPeriodId <= 0)
                {
                    TempData["Error"] = "Please choose a rent period to mark as paid.";
                    return RedirectToRentPeriodSource(tenancyId, returnUrl);
                }

                await _api.PostAsync<MarkRentPeriodPaidRequest, JsonElement>(
                    $"payments/rent-periods/{rentPeriodId}/mark-paid",
                    new MarkRentPeriodPaidRequest
                    {
                        PaidDate = DateTimeOffset.UtcNow,
                        Note = "Marked paid by landlord as cash/off-platform rent."
                    });

                TempData["Success"] = "Rent period marked as paid. A system receipt was generated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mark rent period paid failed in portal for tenancy {TenancyId}, rent period {RentPeriodId}.", tenancyId, rentPeriodId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to mark the rent period as paid right now.");
            }

            return RedirectToRentPeriodSource(tenancyId, returnUrl);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkRentPeriodsPaid(
            int tenancyId,
            string? rentPeriodIds,
            string? returnUrl = null)
        {
            try
            {
                var ids = (rentPeriodIds ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => int.TryParse(value, out var id) ? id : 0)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
                if (ids.Count == 0)
                {
                    TempData["Error"] = "Please choose at least one rent period to mark as paid.";
                    return RedirectToRentPeriodSource(tenancyId, returnUrl);
                }

                await _api.PostAsync<MarkRentPeriodsPaidRequest, JsonElement>(
                    "payments/rent-periods/mark-paid",
                    new MarkRentPeriodsPaidRequest
                    {
                        RentPeriodIds = ids,
                        PaidDate = DateTimeOffset.UtcNow,
                        Note = "Marked paid as one cash/off-platform rent payment."
                    });

                TempData["Success"] = ids.Count == 1
                    ? "Rent period marked as paid. A system receipt was generated."
                    : $"{ids.Count} rent periods were marked as paid. One itemized system receipt was generated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mark grouped rent periods paid failed in portal for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to mark the selected rent periods as paid right now.");
            }

            return RedirectToRentPeriodSource(tenancyId, returnUrl);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ArchivePaidBeforePlatformPeriods(
            int tenancyId,
            string? returnUrl = null)
        {
            try
            {
                await _api.PostAsync<object, JsonElement>(
                    $"tenancies/{tenancyId}/rent-periods/archive-paid-before-platform",
                    new { });
                TempData["Success"] = "Historical periods paid before Lontsi Homes were archived. They will not be regenerated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Historical rent period archival failed for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to archive the historical rent periods right now.");
            }

            return RedirectToRentPeriodSource(tenancyId, returnUrl);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ReconcileLegacyRentSchedule(
            int tenancyId,
            int rentDueDay,
            int paymentIntervalMonths,
            DateTimeOffset firstTrackedPeriodStart,
            string? returnUrl = null)
        {
            try
            {
                await _api.PostAsync<ReconcileRentScheduleRequest, JsonElement>(
                    $"tenancies/{tenancyId}/rent-schedule/reconcile",
                    new ReconcileRentScheduleRequest
                    {
                        RentDueDay = rentDueDay,
                        PaymentIntervalMonths = paymentIntervalMonths,
                        FirstTrackedPeriodStart = firstTrackedPeriodStart
                    });
                TempData["Success"] = "The rent schedule was corrected. Automatic reminders can resume on the confirmed dates.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy rent schedule correction failed for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to correct the rent schedule right now.");
            }

            return RedirectToRentPeriodSource(tenancyId, returnUrl);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SendManualRentReminder(int tenancyId, string? returnUrl = null)
        {
            try
            {
                var result = await _api.PostAsync<object, SendManualRentReminderResultDto>(
                    $"tenancies/{tenancyId}/rent-reminders/manual",
                    new { });

                TempData["Success"] = result.Status == RentReminderStatusEnum.Sent
                    ? "Manual rent reminder sent."
                    : "The reminder was recorded and queued for another delivery attempt.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual rent reminder failed in portal for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to send the rent reminder right now.");
            }

            return RedirectToRentPeriodSource(tenancyId, returnUrl);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelPendingRentPayment(int tenancyId, int rentPeriodId, string? returnUrl = null)
        {
            try
            {
                if (rentPeriodId <= 0)
                {
                    TempData["Error"] = "Please choose a pending rent payment to cancel.";
                    return RedirectToRentPeriodSource(tenancyId, returnUrl);
                }

                await _api.PostAsync(
                    $"payments/rent-periods/{rentPeriodId}/cancel-pending-payment",
                    new { });

                TempData["Success"] = "Pending payment cancelled. The rent period is available again.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cancel pending rent payment failed in portal for tenancy {TenancyId}, rent period {RentPeriodId}.", tenancyId, rentPeriodId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to cancel the pending payment right now.");
            }

            return RedirectToRentPeriodSource(tenancyId, returnUrl);
        }

        private async Task<TenancyCreateDraft> LoadOrCreateDraftAsync(int apartmentId)
        {
            var existing = LoadDraft(apartmentId);
            if (existing != null)
            {
                if (existing.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth)
                {
                    existing.EndBehavior = TenancyEndBehaviorEnum.NoEndDate;
                    existing.EndDate = null;
                }

                existing.PaymentIntervalMonths = Math.Clamp(existing.PaymentIntervalMonths, 1, 12);
                if (existing.RentTrackingStartDate == default)
                {
                    existing.RentTrackingStartDate = existing.StartDate;
                }

                if (!existing.HasSelectedImportMode)
                {
                    existing.ImportMode = null;
                }

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
                RentDueDay = today.Day,
                PaymentIntervalMonths = 1,
                EndBehavior = TenancyEndBehaviorEnum.NoEndDate,
                RentTrackingStartDate = today,
                RentReminderRules = overview.Apartment.RentReminderRules
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
            var trackingStart = draft.StartDate;
            if (draft.ImportMode == RentPaymentImportModeEnum.AllPastPeriodsPaidBeforeRentHub)
            {
                trackingStart = RentPeriodScheduleHelper.ResolveNextBillingGroupStart(
                    draft.StartDate,
                    DateTimeOffset.UtcNow,
                    draft.PaymentIntervalMonths);
            }
            else if (draft.ImportMode is RentPaymentImportModeEnum.SomePeriodsWerePaid or
                     RentPaymentImportModeEnum.TenantPaidInAdvance &&
                     draft.UnpaidFrom.HasValue)
            {
                trackingStart = draft.UnpaidFrom.Value;
            }

            draft.RentPeriods = RentPeriodScheduleHelper.GeneratePeriods(
                draft.StartDate,
                draft.EndDate,
                draft.EndBehavior,
                draft.MonthlyRent,
                draft.RentDueDay,
                DateTimeOffset.UtcNow,
                draft.PaymentIntervalMonths,
                trackingStart);

            draft.RentTrackingStartDate = draft.RentPeriods
                .OrderBy(period => period.PeriodStart)
                .Select(period => period.PeriodStart)
                .FirstOrDefault();

            if (draft.RentTrackingStartDate == default)
            {
                draft.RentTrackingStartDate = RentPeriodScheduleHelper.ResolveNextBillingGroupStart(
                    draft.StartDate,
                    DateTimeOffset.UtcNow,
                    draft.PaymentIntervalMonths);
            }
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

            errors.AddRange(RentPeriodScheduleHelper.ValidateGeneratedSchedule(
                draft.RentPeriods,
                draft.StartDate,
                draft.EndDate,
                draft.EndBehavior,
                draft.PaymentIntervalMonths,
                draft.RentTrackingStartDate));

            if (errors.Any())
            {
                return errors;
            }

            if (!draft.HasSelectedImportMode || !draft.ImportMode.HasValue)
            {
                errors.Add("Please choose how existing rent periods should be treated.");
                return errors;
            }

            if (draft.ImportMode is RentPaymentImportModeEnum.SomePeriodsWerePaid or
                RentPaymentImportModeEnum.TenantPaidInAdvance)
            {
                if (!draft.UnpaidFrom.HasValue)
                {
                    errors.Add("Please choose the first monthly period that is not covered.");
                }
                else if (!RentPeriodScheduleHelper.IsMonthlyBoundary(draft.StartDate, draft.UnpaidFrom.Value))
                {
                    errors.Add("The first uncovered period must start on a monthly boundary calculated by the system.");
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

            if (draft.PaymentIntervalMonths is < 1 or > 12)
            {
                errors.Add("Payment interval must be between 1 and 12 months.");
            }

            if (draft.EndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !draft.EndDate.HasValue)
            {
                errors.Add("An automatically expiring tenancy requires an end date.");
            }

            if (string.IsNullOrWhiteSpace(draft.MainTenant.Email))
            {
                errors.Add("Please invite the main tenant before creating the tenancy.");
            }

            errors.AddRange(RentPeriodScheduleHelper.ValidateGeneratedSchedule(
                draft.RentPeriods,
                draft.StartDate,
                draft.EndDate,
                draft.EndBehavior,
                draft.PaymentIntervalMonths,
                draft.RentTrackingStartDate));

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

        private async Task<TenancyOverviewVm> BuildOverviewVmAsync(
            int id,
            string? memberSearch,
            string? rentStatus = null,
            DateTime? rentFrom = null,
            DateTime? rentTo = null,
            int rentPage = 1,
            int rentPageSize = 5,
            string rentSortDirection = "priority",
            bool applySmartDefaultRentStatus = false,
            int rentGroupPage = 1)
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

            var allRentPeriods = (overview.RentPeriods ?? new List<RentPeriodDto>())
                .OrderBy(period => period.PeriodStart)
                .ToList();
            if (applySmartDefaultRentStatus && string.IsNullOrWhiteSpace(rentStatus))
            {
                rentStatus = AllRentStatusFilter;
            }

            var rentStatuses = allRentPeriods
                .Where(period => period.Status != RentPeriodStatusEnum.NotDueYet)
                .Select(period => period.StatusLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label)
                .ToList();
            var filteredRentPeriods = allRentPeriods.AsEnumerable();
            if (string.Equals(rentStatus, UnpaidRentStatusFilter, StringComparison.OrdinalIgnoreCase))
            {
                filteredRentPeriods = filteredRentPeriods.Where(period => IsCurrentlyUnpaid(period.Status));
            }
            else if (string.Equals(rentStatus, FutureRentStatusFilter, StringComparison.OrdinalIgnoreCase))
            {
                filteredRentPeriods = filteredRentPeriods.Where(period =>
                    period.Status == RentPeriodStatusEnum.NotDueYet);
            }
            else if (!string.IsNullOrWhiteSpace(rentStatus) &&
                     !string.Equals(rentStatus, AllRentStatusFilter, StringComparison.OrdinalIgnoreCase))
            {
                filteredRentPeriods = filteredRentPeriods.Where(period =>
                    string.Equals(period.StatusLabel, rentStatus.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(period.Status.ToString(), rentStatus.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (rentFrom.HasValue)
            {
                filteredRentPeriods = filteredRentPeriods.Where(period => period.PeriodEnd.Date >= rentFrom.Value.Date);
            }
            if (rentTo.HasValue)
            {
                filteredRentPeriods = filteredRentPeriods.Where(period => period.PeriodStart.Date <= rentTo.Value.Date);
            }

            rentSortDirection = rentSortDirection?.Trim().ToLowerInvariant() switch
            {
                "asc" => "asc",
                "desc" => "desc",
                _ => "priority"
            };
            filteredRentPeriods = rentSortDirection switch
            {
                "desc" => filteredRentPeriods
                    .OrderBy(GetRentDisplayPriority)
                    .ThenByDescending(period => period.PeriodStart),
                "asc" => filteredRentPeriods
                    .OrderBy(GetRentDisplayPriority)
                    .ThenBy(period => period.PeriodStart),
                _ => filteredRentPeriods
                    .OrderBy(GetRentDisplayPriority)
                    .ThenBy(period => GetRentDisplayPriority(period) < 2
                        ? period.PeriodStart.UtcDateTime.Ticks
                        : -period.PeriodStart.UtcDateTime.Ticks)
            };

            rentPageSize = Math.Clamp(rentPageSize, 5, 50);
            var filteredList = filteredRentPeriods.ToList();
            var totalRentPages = Math.Max(1, (int)Math.Ceiling(filteredList.Count / (double)rentPageSize));
            rentPage = Math.Clamp(rentPage, 1, totalRentPages);
            const int rentGroupPageSize = 5;
            var totalRentGroups = allRentPeriods
                .GroupBy(period => period.BillingGroupSequence)
                .Count(group => group.Any(period => GetRentDisplayPriority(period) < 2));
            var totalRentGroupPages = Math.Max(1, (int)Math.Ceiling(totalRentGroups / (double)rentGroupPageSize));
            rentGroupPage = Math.Clamp(rentGroupPage, 1, totalRentGroupPages);

            return new TenancyOverviewVm
            {
                Tenancy = overview.Tenancy,
                Members = members,
                Documents = (overview.Documents ?? new List<DocumentDto>())
                    .Where(doc => doc.DocumentType == DocumentTypeEnum.TenancyContract)
                    .OrderByDescending(doc => doc.UploadedAt)
                    .ToList(),
                RentPeriods = filteredList.Skip((rentPage - 1) * rentPageSize).Take(rentPageSize).ToList(),
                AllRentPeriods = allRentPeriods,
                RentSummary = overview.RentSummary ?? new RentSummaryDto(),
                ReminderHistory = overview.ReminderHistory ?? new List<RentReminderHistoryDto>(),
                MemberSearch = memberSearch,
                RentStatus = rentStatus,
                RentFrom = rentFrom,
                RentTo = rentTo,
                RentSortDirection = rentSortDirection,
                RentPage = rentPage,
                RentPageSize = rentPageSize,
                TotalRentPeriods = filteredList.Count,
                TotalRentPages = totalRentPages,
                RentGroupPage = rentGroupPage,
                RentGroupPageSize = rentGroupPageSize,
                TotalRentGroups = totalRentGroups,
                TotalRentGroupPages = totalRentGroupPages,
                RentStatuses = rentStatuses
            };
        }

        private static bool IsCurrentlyUnpaid(RentPeriodStatusEnum status)
        {
            return status is RentPeriodStatusEnum.Due
                or RentPeriodStatusEnum.Overdue
                or RentPeriodStatusEnum.PendingPayment;
        }

        private static int GetRentDisplayPriority(RentPeriodDto period)
        {
            if (IsCurrentlyUnpaid(period.Status))
            {
                return 0;
            }

            if (period.Status == RentPeriodStatusEnum.NotDueYet)
            {
                return 1;
            }

            return 2;
        }

        private IActionResult RedirectToRentPeriodSource(int tenancyId, string? returnUrl)
        {
            return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? LocalRedirect(returnUrl)
                : RedirectToAction(nameof(Overview), new { id = tenancyId });
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
