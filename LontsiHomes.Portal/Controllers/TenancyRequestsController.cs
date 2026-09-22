using System.Text.Json;
using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.Portal.Hubs;
using LontsiHomes.Portal.Services;
using LontsiHomes.Portal.ViewModels.Tenancies;

namespace LontsiHomes.Portal.Controllers
{
    [Authorize(Roles = "Admin,Landlord,Manager,Tenant,Owner")]
    public class TenancyRequestsController : Controller
    {
        private readonly LontsiHomesApiClient _api;
        private readonly ILogger<TenancyRequestsController> _logger;
        private readonly IHubContext<WorkspaceHub> _workspaceHub;

        public TenancyRequestsController(
            LontsiHomesApiClient api,
            ILogger<TenancyRequestsController> logger,
            IHubContext<WorkspaceHub> workspaceHub)
        {
            _api = api;
            _logger = logger;
            _workspaceHub = workspaceHub;
        }

        [HttpGet]
        public async Task<IActionResult> PendingCount()
        {
            try
            {
                var result = await _api.GetAsync<TenancyRequestPendingCountDto>("tenancy-requests/pending-count");
                return Json(result);
            }
            catch
            {
                return Json(new TenancyRequestPendingCountDto());
            }
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            int? tenancyId = null,
            string? requestType = null,
            string? status = null,
            int? propertyId = null,
            int? apartmentId = null,
            bool openForm = false,
            int page = 1,
            int pageSize = 12)
        {
            try
            {
                var endpoint = $"tenancy-requests?type={Uri.EscapeDataString(requestType ?? string.Empty)}" +
                               $"&status={Uri.EscapeDataString(status ?? string.Empty)}" +
                               $"&propertyId={propertyId}&apartmentId={apartmentId}&page={page}&pageSize={pageSize}";
                var vm = new TenancyRequestsVm
                {
                    Requests = await _api.GetAsync<TenancyRequestListDto>(endpoint),
                    RequestType = requestType,
                    Status = status,
                    PropertyId = propertyId,
                    ApartmentId = apartmentId,
                    PageSize = pageSize,
                    OpenRequestForm = openForm
                };

                var selectedTenancyId = tenancyId is > 0
                    ? tenancyId
                    : vm.Requests.RequestableTenancies.FirstOrDefault()?.TenancyId;
                if (selectedTenancyId is > 0)
                {
                    var overview = await _api.GetAsync<TenancyOverviewDto>($"tenancies/{selectedTenancyId}/overview");
                    vm.SelectedTenancy = overview.Tenancy;
                    if (overview.Tenancy.CanRequestRenewal)
                    {
                        vm.RenewalWorkspace = await _api.GetAsync<TenancyRenewalWorkspaceDto>($"tenancies/{selectedTenancyId}/extension-requests");
                        vm.ProposedEndDate = vm.RenewalWorkspace.MinimumProposedEndDate?.LocalDateTime;
                    }

                    vm.RequestedEndDate = ResolveSuggestedTerminationDate(overview.Tenancy);
                }

                return View(vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load the tenancy request register.");
                TempData["Error"] = UserMessage(ex, "Unable to load tenancy requests right now.");
                return RedirectToAction("Index", "Tenancies");
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitTermination(
            int tenancyId,
            DateTime? requestedEndDate,
            string? terminationReason)
        {
            if (tenancyId <= 0) return RedirectToAction(nameof(Index));
            if (!requestedEndDate.HasValue)
            {
                TempData["Error"] = "Choose the requested tenancy end date.";
                return RedirectToAction(nameof(Index), new { tenancyId, openForm = true });
            }
            if (terminationReason?.Length > 1000)
            {
                TempData["Error"] = "The request reason cannot exceed 1000 characters.";
                return RedirectToAction(nameof(Index), new { tenancyId, openForm = true });
            }

            try
            {
                await _api.PostAsync($"tenancies/{tenancyId}/termination-requests", new CreateTenancyTerminationRequest
                {
                    RequestedEndDate = new DateTimeOffset(requestedEndDate.Value.Date, TimeSpan.Zero),
                    Reason = terminationReason
                });
                TempData["Success"] = "Tenancy end request submitted.";
                await NotifyTenancyRequestsChangedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to submit a tenancy end request for tenancy {TenancyId}.", tenancyId);
                TempData["Error"] = UserMessage(ex, "Unable to submit the tenancy end request right now.");
            }

            return RedirectToAction(nameof(Index), new { tenancyId, openForm = true });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitRenewal(int tenancyId, DateTime? proposedEndDate)
        {
            if (tenancyId <= 0) return RedirectToAction(nameof(Index));
            if (!proposedEndDate.HasValue)
            {
                TempData["Error"] = "Choose a proposed end date.";
                return RedirectToAction(nameof(Index), new { tenancyId, openForm = true });
            }

            try
            {
                await _api.PostAsync($"tenancies/{tenancyId}/extension-requests", new ExtendTenancyRequest
                {
                    NewEndDate = new DateTimeOffset(proposedEndDate.Value.Date, TimeSpan.Zero)
                });
                TempData["Success"] = "Renewal request submitted.";
                await NotifyTenancyRequestsChangedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to submit a renewal request for tenancy {TenancyId}.", tenancyId);
                TempData["Error"] = UserMessage(ex, "Unable to submit the renewal request right now.");
            }

            return RedirectToAction(nameof(Index), new { tenancyId, openForm = true });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(
            int tenancyId,
            int requestId,
            TenancyRequestTypeEnum requestType)
        {
            if (tenancyId <= 0 || requestId <= 0 || !Enum.IsDefined(requestType))
                return RedirectToAction(nameof(Index));

            try
            {
                var endpoint = requestType == TenancyRequestTypeEnum.Renewal
                    ? $"tenancies/{tenancyId}/extension-requests/{requestId}/approve"
                    : $"tenancies/{tenancyId}/termination-requests/{requestId}/approve";
                await _api.PutAsync(endpoint, new { });
                TempData["Success"] = requestType == TenancyRequestTypeEnum.Renewal
                    ? "Renewal request approved."
                    : "Tenancy end request approved.";
                await NotifyTenancyRequestsChangedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to approve tenancy request {RequestId}.", requestId);
                TempData["Error"] = UserMessage(ex, "Unable to approve the request right now.");
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reject(
            int tenancyId,
            int requestId,
            TenancyRequestTypeEnum requestType,
            string? rejectionReason)
        {
            if (tenancyId <= 0 || requestId <= 0 || !Enum.IsDefined(requestType))
                return RedirectToAction(nameof(Index));
            if (string.IsNullOrWhiteSpace(rejectionReason))
            {
                TempData["Error"] = "A rejection reason is required.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                if (requestType == TenancyRequestTypeEnum.Renewal)
                {
                    if (rejectionReason.Length > 512)
                    {
                        TempData["Error"] = "The rejection reason cannot exceed 512 characters.";
                        return RedirectToAction(nameof(Index));
                    }
                    await _api.PutAsync($"tenancies/{tenancyId}/extension-requests/{requestId}/reject", new RejectTenancyExtensionRequest
                    {
                        Reason = rejectionReason.Trim()
                    });
                }
                else
                {
                    if (rejectionReason.Length > 1000)
                    {
                        TempData["Error"] = "The rejection reason cannot exceed 1000 characters.";
                        return RedirectToAction(nameof(Index));
                    }
                    await _api.PutAsync($"tenancies/{tenancyId}/termination-requests/{requestId}/reject", new RejectTenancyTerminationRequest
                    {
                        Reason = rejectionReason.Trim()
                    });
                }

                TempData["Success"] = requestType == TenancyRequestTypeEnum.Renewal
                    ? "Renewal request rejected."
                    : "Tenancy end request rejected.";
                await NotifyTenancyRequestsChangedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to reject tenancy request {RequestId}.", requestId);
                TempData["Error"] = UserMessage(ex, "Unable to reject the request right now.");
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelTermination(
            int tenancyId,
            int requestId,
            TenancyTerminationCancellationModeEnum continuationMode,
            DateTime? replacementStartDate,
            string? endMode,
            DateTime? newEndDate,
            string? cancellationNote)
        {
            if (tenancyId <= 0 || requestId <= 0 || !Enum.IsDefined(continuationMode))
                return RedirectToAction(nameof(Index));

            var fixedEnd = string.Equals(endMode, "Fixed", StringComparison.OrdinalIgnoreCase);
            if (fixedEnd && !newEndDate.HasValue)
            {
                TempData["Error"] = "Choose the new tenancy end date or select an open-ended tenancy.";
                return RedirectToAction(nameof(Index));
            }
            if (continuationMode == TenancyTerminationCancellationModeEnum.StartReplacementTenancy &&
                !replacementStartDate.HasValue)
            {
                TempData["Error"] = "Choose the replacement tenancy start date.";
                return RedirectToAction(nameof(Index));
            }
            if (cancellationNote?.Length > 1000)
            {
                TempData["Error"] = "The cancellation note cannot exceed 1000 characters.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                await _api.PutAsync($"tenancies/{tenancyId}/termination-requests/{requestId}/cancel", new CancelTenancyTerminationRequest
                {
                    Mode = continuationMode,
                    ReplacementStartDate = replacementStartDate.HasValue
                        ? new DateTimeOffset(replacementStartDate.Value.Date, TimeSpan.Zero)
                        : null,
                    NewEndDate = fixedEnd && newEndDate.HasValue
                        ? new DateTimeOffset(newEndDate.Value.Date, TimeSpan.Zero)
                        : null,
                    Note = cancellationNote
                });
                TempData["Success"] = "Tenancy termination decision cancelled.";
                await NotifyTenancyRequestsChangedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to cancel tenancy termination decision {RequestId}.", requestId);
                TempData["Error"] = UserMessage(ex, "Unable to cancel the termination decision right now.");
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(
            int tenancyId,
            int requestId,
            TenancyRequestTypeEnum requestType)
        {
            if (tenancyId <= 0 || requestId <= 0 || !Enum.IsDefined(requestType))
                return RedirectToAction(nameof(Index));

            try
            {
                var endpoint = requestType == TenancyRequestTypeEnum.Renewal
                    ? $"tenancies/{tenancyId}/extension-requests/{requestId}/withdraw"
                    : $"tenancies/{tenancyId}/termination-requests/{requestId}/withdraw";
                await _api.PutAsync(endpoint, new { });
                TempData["Success"] = requestType == TenancyRequestTypeEnum.Renewal
                    ? "Renewal request cancelled."
                    : "Tenancy end request cancelled.";
                await NotifyTenancyRequestsChangedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to cancel tenancy request {RequestId}.", requestId);
                TempData["Error"] = UserMessage(ex, "Unable to cancel the request right now.");
            }

            return RedirectToAction(nameof(Index));
        }

        private static DateTime ResolveSuggestedTerminationDate(TenancyDetailsDto tenancy)
        {
            var today = DateTime.UtcNow.Date;
            if (!tenancy.EndDate.HasValue) return today.AddMonths(1);
            var dayBeforeCurrentEnd = tenancy.EndDate.Value.UtcDateTime.Date.AddDays(-1);
            return dayBeforeCurrentEnd < today ? today : dayBeforeCurrentEnd;
        }

        private Task NotifyTenancyRequestsChangedAsync()
        {
            return _workspaceHub.Clients.All.SendAsync("TenancyRequestsChanged", new
            {
                changedAt = DateTimeOffset.UtcNow
            });
        }

        private static string UserMessage(Exception ex, string fallback)
        {
            try
            {
                using var document = JsonDocument.Parse(ex.Message);
                if (document.RootElement.TryGetProperty("Message", out var message) ||
                    document.RootElement.TryGetProperty("message", out message))
                {
                    return string.IsNullOrWhiteSpace(message.GetString()) ? fallback : message.GetString()!;
                }
            }
            catch (JsonException)
            {
            }

            return fallback;
        }
    }
}
