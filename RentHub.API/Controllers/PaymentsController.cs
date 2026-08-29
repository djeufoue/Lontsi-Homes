using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using Common.Enums;
using RentHub.API.Services.Payments;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using RentHub.API.Helpers;
using Common.Helpers;
using RentHub.API.Services.Receipts;
using RentHub.API.Services.Permissions;
using System.Data;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PaymentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPaymentService _orangeMoneyService;
        private readonly MomoService _momoService;
        private readonly CardPaymentService _cardPaymentService;
        private readonly IStripeCheckoutService _stripeCheckoutService;
        private readonly IRentReceiptService _receiptService;
        private readonly IConfiguration _configuration;
        private readonly IManagerPermissionService _permissionService;

        public PaymentsController(ApplicationDbContext context,
            IPaymentService orangeMoneyService,
            MomoService momoService,
            CardPaymentService cardPaymentService,
            IStripeCheckoutService stripeCheckoutService,
            IRentReceiptService receiptService,
            IConfiguration configuration,
            IManagerPermissionService permissionService)
        {
            _context = context;
            _orangeMoneyService = orangeMoneyService;
            _momoService = momoService;
            _cardPaymentService = cardPaymentService;
            _stripeCheckoutService = stripeCheckoutService;
            _receiptService = receiptService;
            _configuration = configuration;
            _permissionService = permissionService;
        }

        /// <summary>
        /// Initiates a payment from a tenant to a landlord using the specified method.
        /// The caller must provide valid tenant and landlord identifiers as well as
        /// the amount and phone numbers (for mobile money).
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreatePayment([FromBody] PaymentRequest request)
        {
            try
            {
                // Validate tenant and landlord IDs
                if (string.IsNullOrEmpty(request.TenantId) || string.IsNullOrEmpty(request.LandlordId))
                {
                    return BadRequest("TenantId and LandlordId are required.");
                }
                // Ensure number of periods is at least 1
                if (request.NumberOfPeriods <= 0)
                {
                    return BadRequest("NumberOfPeriods must be at least 1.");
                }
                // Verify existence of tenant and landlord
                var tenant = await _context.Users.FindAsync(request.TenantId);
                var landlord = await _context.Users.FindAsync(request.LandlordId);
                if (tenant == null || landlord == null)
                {
                    return NotFound("Tenant or landlord not found.");
                }
                // Determine rent amount from the tenancy, if present
                // Find the active tenancy between this tenant and landlord
                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a!.Property)
                    .ThenInclude(p => p!.Landlord)
                    .Include(t => t.Members)
                     .FirstOrDefaultAsync(t => t.Members.Any(m => !m.IsDeleted && m.MemberId == request.TenantId) && t.Apartment != null && t.Apartment!.Property!.LandlordId == request.LandlordId &&
                        (t.EndDate == null || t.EndDate >= DateTime.UtcNow) && t.StartDate <= DateTime.UtcNow);
                decimal finalAmount;
                if (tenancy != null)
                {
                    if (!await PaymentAvailabilityHelper.IsAutomaticPaymentEnabledForPropertyAsync(_context, tenancy.Apartment!.PropertyId))
                    {
                        return StatusCode(StatusCodes.Status409Conflict, new
                        {
                            Code = "AUTOMATIC_PAYMENTS_DISABLED",
                            Message = PaymentAvailabilityHelper.AutomaticPaymentsUnavailableMessage
                        });
                    }

                    finalAmount = tenancy.MonthlyRent * request.NumberOfPeriods;
                }
                else
                {
                    // If no active tenancy is found, fall back to provided amount if positive
                    if (request.Amount <= 0)
                    {
                        return BadRequest("No active tenancy found and amount is invalid. Provide a positive amount or ensure tenancy exists.");
                    }
                    finalAmount = request.Amount;
                }
                // Extract current user ID (the caller)
                var currentUserId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Unauthorized();
                }

                if (!string.Equals(currentUserId, request.TenantId, StringComparison.Ordinal))
                {
                    return Forbid();
                }

                if (tenancy?.Apartment?.Property?.Landlord != null)
                {
                    var paymentAvailability = TenanciesController.ResolveTenantRentPaymentAvailability(
                        tenancy.Apartment.Property.Landlord,
                        tenancy.Apartment.Property.CountryIsoCode,
                        tenancy.Apartment.Property.CountryCode);
                    if (!paymentAvailability.CanPay)
                    {
                        return BadRequest(new { Message = paymentAvailability.Message });
                    }

                    if (request.Method != paymentAvailability.Method)
                    {
                        return BadRequest(new { Message = BuildPaymentMethodMismatchMessage(paymentAvailability.Method) });
                    }
                }

                var requestKey = BuildPaymentRequestKey(request, currentUserId, tenancy?.Id, finalAmount);
                var existingPayment = await _context.Payments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.RequestKey == requestKey && !p.IsDeleted);

                if (existingPayment != null)
                {
                    if (request.Method == PaymentMethodEnum.Card &&
                        existingPayment.Status == PaymentStatusEnum.Pending)
                    {
                        var existingPeriods = await _context.RentPeriods
                            .Where(period => period.PaymentId == existingPayment.Id && !period.IsDeleted)
                            .OrderBy(period => period.PeriodStart)
                            .ToListAsync();

                        return Ok(BuildRentCheckoutSessionDto(
                            existingPayment,
                            tenancy,
                            existingPeriods,
                            chargeAmount: ConvertXafToStripeMinorUnits(existingPayment.Amount) / 100m,
                            chargeCurrency: ResolveStripeCurrency(),
                            clientSecret: string.Empty,
                            publishableKey: ResolveStripePublishableKey(),
                            providerReference: existingPayment.TransactionId));
                    }

                    return Ok(new
                    {
                        existingPayment.Id,
                        Status = existingPayment.Status.ToString(),
                        existingPayment.TransactionId,
                        Duplicate = true
                    });
                }

                // Create payment record with audit info
                var payment = new Payment
                {
                    TenantId = request.TenantId,
                    LandlordId = request.LandlordId,
                    Amount = finalAmount,
                    Currency = "XAF",
                    Method = request.Method,
                    RequestKey = requestKey,
                    Status = PaymentStatusEnum.Pending,
                    CreatedBy = currentUserId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };
                // Associate payment with the tenancy if available
                if (tenancy != null)
                {
                    payment.TenancyId = tenancy.Id;
                }
                _context.Payments.Add(payment);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                {
                    existingPayment = await _context.Payments
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(p => p.RequestKey == requestKey && !p.IsDeleted);

                    if (existingPayment != null)
                    {
                        return Ok(new
                        {
                            existingPayment.Id,
                            Status = existingPayment.Status.ToString(),
                            existingPayment.TransactionId,
                            Duplicate = true
                        });
                    }

                    throw;
                }

                // Set request amount for processing to finalAmount to ensure payment services use this value
                request.Amount = finalAmount;
                // Determine which service to use
                PaymentResult result;
                switch (request.Method)
                {
                    case PaymentMethodEnum.OrangeMoney:
                        result = await _orangeMoneyService.ProcessPaymentAsync(payment, request);
                        break;
                    case PaymentMethodEnum.Momo:
                        result = await _momoService.ProcessPaymentAsync(payment, request);
                        break;
                    case PaymentMethodEnum.Card:
                        result = await _cardPaymentService.ProcessPaymentAsync(payment, request);
                        break;
                    default:
                        result = new PaymentResult { Success = false, Status = "UNKNOWN" };
                        break;
                }
                // Update payment record with final status and audit info
                if (Enum.TryParse<PaymentStatusEnum>(result.Status, true, out var statusEnum))
                {
                    payment.Status = statusEnum;
                }

                if (!string.IsNullOrWhiteSpace(result.TransactionId))
                {
                    payment.TransactionId = result.TransactionId;
                }

                payment.ProviderReceiptUrl = result.ProviderReceiptUrl;

                // Mark update fields
                payment.UpdatedBy = currentUserId;
                payment.UpdatedAt = DateTime.UtcNow;
                _context.Payments.Update(payment);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex) && !string.IsNullOrWhiteSpace(result.TransactionId))
                {
                    existingPayment = await _context.Payments
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(p => p.TransactionId == result.TransactionId && !p.IsDeleted);

                    if (existingPayment != null)
                    {
                        return Ok(new
                        {
                            existingPayment.Id,
                            Status = existingPayment.Status.ToString(),
                            existingPayment.TransactionId,
                            Duplicate = true
                        });
                    }

                    throw;
                }

                if (!result.Success)
                {
                    return BadRequest(new { payment.Id, result.Status, result.ProviderResponse, Duplicate = false });
                }
                return Ok(new { payment.Id, result.Status, payment.TransactionId, Duplicate = false });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("rent-periods")]
        [Authorize]
        public async Task<IActionResult> PayRentPeriods([FromBody] PayRentPeriodsRequest request)
        {
            try
            {
                if (request.TenancyId <= 0)
                {
                    return BadRequest("TenancyId is required.");
                }

                var currentUserId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Unauthorized();
                }

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a!.Property)
                    .ThenInclude(p => p!.Landlord)
                    .Include(t => t.Members)
                    .Include(t => t.RentPeriods)
                    .FirstOrDefaultAsync(t => t.Id == request.TenancyId && !t.IsDeleted);

                if (tenancy == null)
                {
                    return NotFound("Tenancy not found.");
                }

                if (tenancy.Apartment?.Property == null)
                {
                    return NotFound("Property not found.");
                }

                if (!await PaymentAvailabilityHelper.IsAutomaticPaymentEnabledForPropertyAsync(
                        _context,
                        tenancy.Apartment.PropertyId))
                {
                    return StatusCode(StatusCodes.Status409Conflict, new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = PaymentAvailabilityHelper.AutomaticPaymentsUnavailableMessage
                    });
                }

                var isTenantMember = tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == currentUserId);
                if (!isTenantMember)
                {
                    return Forbid();
                }

                var paymentAvailability = TenanciesController.ResolveTenantRentPaymentAvailability(
                    tenancy.Apartment.Property.Landlord,
                    tenancy.Apartment.Property.CountryIsoCode,
                    tenancy.Apartment.Property.CountryCode);
                if (!paymentAvailability.CanPay)
                {
                    return BadRequest(new { Message = paymentAvailability.Message });
                }

                if (request.Method != paymentAvailability.Method)
                {
                    return BadRequest(new { Message = BuildPaymentMethodMismatchMessage(paymentAvailability.Method) });
                }

                var firstOpenGroup = RentPaymentGroupHelper.SelectOldestOutstandingGroup(tenancy.RentPeriods);
                if (firstOpenGroup.Count == 0)
                {
                    return BadRequest("There is no unpaid rent period to pay.");
                }

                var pendingPaymentPeriod = firstOpenGroup
                    .FirstOrDefault(period => period.Status == RentPeriodStatusEnum.PendingPayment);
                if (pendingPaymentPeriod != null)
                {
                    if (request.Method == PaymentMethodEnum.Card && pendingPaymentPeriod.PaymentId.HasValue)
                    {
                        var pendingPayment = await _context.Payments
                            .IgnoreQueryFilters()
                            .FirstOrDefaultAsync(payment =>
                                payment.Id == pendingPaymentPeriod.PaymentId.Value &&
                                payment.TenantId == currentUserId &&
                                payment.Status == PaymentStatusEnum.Pending &&
                                !payment.IsDeleted);

                        if (pendingPayment != null && IsStripeCheckoutSessionId(pendingPayment.TransactionId))
                        {
                            var pendingPeriods = firstOpenGroup
                                .Where(period => period.PaymentId == pendingPayment.Id)
                                .OrderBy(period => period.PeriodStart)
                                .ToList();

                            return Ok(BuildRentCheckoutSessionDto(
                                pendingPayment,
                                tenancy,
                                pendingPeriods,
                                chargeAmount: ConvertXafToStripeMinorUnits(pendingPayment.Amount) / 100m,
                                chargeCurrency: ResolveStripeCurrency(),
                                clientSecret: string.Empty,
                                publishableKey: ResolveStripePublishableKey(),
                                providerReference: pendingPayment.TransactionId));
                        }
                    }

                    return BadRequest(new { Message = "A previous rent payment is still pending. Please complete or retry that payment before paying another period." });
                }

                var payablePeriods = firstOpenGroup;

                var totalAmount = payablePeriods.Sum(period => period.Amount - period.PaidAmount);
                if (totalAmount <= 0)
                {
                    return BadRequest("The selected rent periods do not have a positive amount due.");
                }

                var paymentRequest = new PaymentRequest
                {
                    TenantId = currentUserId,
                    LandlordId = tenancy.Apartment.Property.LandlordId,
                    Amount = totalAmount,
                    Method = request.Method,
                    NumberOfPeriods = payablePeriods.Count,
                    IdempotencyKey = $"rent-periods:{request.TenancyId}:{string.Join(",", payablePeriods.Select(period => period.Id))}"
                };

                var requestKey = BuildPaymentRequestKey(paymentRequest, currentUserId, tenancy.Id, totalAmount);
                var existingPayment = await _context.Payments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.RequestKey == requestKey && !p.IsDeleted);

                if (existingPayment != null)
                {
                    return Ok(new
                    {
                        existingPayment.Id,
                        Status = existingPayment.Status.ToString(),
                        existingPayment.TransactionId,
                        Duplicate = true
                    });
                }

                var payment = new Payment
                {
                    TenantId = currentUserId,
                    LandlordId = tenancy.Apartment.Property.LandlordId,
                    TenancyId = tenancy.Id,
                    Amount = totalAmount,
                    Currency = "XAF",
                    Method = request.Method,
                    RequestKey = requestKey,
                    Status = PaymentStatusEnum.Pending,
                    CreatedBy = currentUserId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };

                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();

                if (request.Method == PaymentMethodEnum.Card)
                {
                    var tenantUser = await _context.Users.FirstOrDefaultAsync(user => user.Id == currentUserId);
                    var landlord = tenancy.Apartment.Property.Landlord;
                    if (tenantUser == null || landlord == null)
                    {
                        return NotFound("Tenant or landlord not found.");
                    }

                    if (string.IsNullOrWhiteSpace(landlord.StripeConnectAccountId))
                    {
                        return BadRequest(new { Message = "Card payment mode is not available because the landlord Stripe account is not ready." });
                    }

                    payment.Tenancy = tenancy;
                    var checkoutCurrency = ResolveStripeCurrency();
                    var checkoutAmountMinorUnits = ConvertXafToStripeMinorUnits(totalAmount);
                    StripeCheckoutResult checkout;
                    try
                    {
                        checkout = await _stripeCheckoutService.CreateRentCheckoutAsync(
                            tenantUser,
                            landlord,
                            payment,
                            payablePeriods,
                            landlord.StripeConnectAccountId,
                            checkoutAmountMinorUnits,
                            checkoutCurrency);
                    }
                    catch
                    {
                        payment.Status = PaymentStatusEnum.Failed;
                        payment.UpdatedBy = currentUserId;
                        payment.UpdatedAt = DateTimeOffset.UtcNow;
                        await _context.SaveChangesAsync();
                        throw;
                    }

                    payment.TransactionId = checkout.SessionId;
                    payment.UpdatedBy = currentUserId;
                    payment.UpdatedAt = DateTimeOffset.UtcNow;

                    foreach (var period in payablePeriods)
                    {
                        period.Status = RentPeriodStatusEnum.PendingPayment;
                        period.PaymentId = payment.Id;
                        period.PaymentReference = checkout.SessionId;
                        period.UpdatedBy = currentUserId;
                        period.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    await _context.SaveChangesAsync();

                    return Ok(BuildRentCheckoutSessionDto(
                        payment,
                        tenancy,
                        payablePeriods,
                        chargeAmount: checkoutAmountMinorUnits / 100m,
                        chargeCurrency: checkoutCurrency,
                        clientSecret: checkout.ClientSecret,
                        publishableKey: ResolveStripePublishableKey(),
                        providerReference: checkout.SessionId,
                        returnUrl: checkout.ReturnUrl));
                }

                PaymentResult result = request.Method switch
                {
                    PaymentMethodEnum.OrangeMoney => await _orangeMoneyService.ProcessPaymentAsync(payment, paymentRequest),
                    PaymentMethodEnum.Momo => await _momoService.ProcessPaymentAsync(payment, paymentRequest),
                    _ => new PaymentResult { Success = false, Status = "UNKNOWN" }
                };

                if (Enum.TryParse<PaymentStatusEnum>(result.Status, true, out var statusEnum))
                {
                    payment.Status = statusEnum;
                }

                if (!string.IsNullOrWhiteSpace(result.TransactionId))
                {
                    payment.TransactionId = result.TransactionId;
                }

                payment.ProviderReceiptUrl = result.ProviderReceiptUrl;
                payment.UpdatedBy = currentUserId;
                payment.UpdatedAt = DateTime.UtcNow;

                if (result.Success)
                {
                    foreach (var period in payablePeriods)
                    {
                        period.Status = RentPeriodStatusEnum.Paid;
                        period.PaidAmount = period.Amount;
                        period.PaidDate = DateTimeOffset.UtcNow;
                        period.PaymentId = payment.Id;
                        period.PaymentReference = payment.TransactionId;
                        period.UpdatedBy = currentUserId;
                        period.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }

                await _context.SaveChangesAsync();

                if (!result.Success)
                {
                    return BadRequest(new { payment.Id, result.Status, result.ProviderResponse, Duplicate = false });
                }

                var receipt = await _receiptService.EnsureReceiptAsync(payment.Id, currentUserId);
                if (receipt != null)
                {
                    await _receiptService.SendReceiptNotificationsAsync(
                        receipt,
                        notifyTenant: true,
                        notifyLandlord: true);
                }

                return Ok(new
                {
                    payment.Id,
                    result.Status,
                    payment.TransactionId,
                    ReceiptNumber = receipt?.ReceiptNumber ?? string.Empty,
                    CoveredPeriodIds = payablePeriods.Select(period => period.Id).ToList(),
                    Duplicate = false
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("rent-periods/checkout-session/{reference}")]
        [Authorize]
        public async Task<IActionResult> GetRentCheckoutSession(string reference)
        {
            try
            {
                var currentUserId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Unauthorized();
                }

                var payment = await _context.Payments
                    .Include(p => p.Tenancy)
                    .ThenInclude(t => t!.Apartment)
                    .ThenInclude(a => a!.Property)
                    .ThenInclude(p => p!.Landlord)
                    .FirstOrDefaultAsync(p =>
                        p.RequestKey == reference &&
                        p.TenantId == currentUserId &&
                        p.Method == PaymentMethodEnum.Card &&
                        !p.IsDeleted);

                if (payment == null)
                {
                    return NotFound("Rent card checkout was not found.");
                }

                var propertyId = payment.Tenancy?.Apartment?.PropertyId;
                if (!propertyId.HasValue ||
                    !await PaymentAvailabilityHelper.IsAutomaticPaymentEnabledForPropertyAsync(_context, propertyId.Value))
                {
                    return StatusCode(StatusCodes.Status409Conflict, new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = PaymentAvailabilityHelper.AutomaticPaymentsUnavailableMessage
                    });
                }

                if (payment.Status == PaymentStatusEnum.Success)
                {
                    return BadRequest(new { Message = "This rent payment has already been completed." });
                }

                if (payment.Status == PaymentStatusEnum.Cancelled)
                {
                    return BadRequest(new { Message = "This rent payment was cancelled. Please choose the rent periods again." });
                }

                if (!IsStripeCheckoutSessionId(payment.TransactionId))
                {
                    return BadRequest(new { Message = "Card checkout is not ready yet. Please choose the rent periods again." });
                }

                var periods = await _context.RentPeriods
                    .Where(period => period.PaymentId == payment.Id && !period.IsDeleted)
                    .OrderBy(period => period.PeriodStart)
                    .ToListAsync();

                var remoteStatus = await _stripeCheckoutService.RetrieveSessionAsync(payment.TransactionId);
                if (remoteStatus == null)
                {
                    return BadRequest(new { Message = "Unable to load the secure card form right now. Please try again." });
                }

                if (!string.IsNullOrWhiteSpace(remoteStatus.PaymentReference) &&
                    !string.Equals(remoteStatus.PaymentReference, payment.RequestKey, StringComparison.Ordinal))
                {
                    return BadRequest(new { Message = "Stripe checkout reference does not match this rent payment." });
                }

                var providerStatus = ResolveStripeProviderStatus(remoteStatus.PaymentStatus, remoteStatus.Status);
                if (IsFailedProviderStatus(providerStatus))
                {
                    await ApplyRentCheckoutStatusAsync(payment, remoteStatus, currentUserId);
                    return BadRequest(new { Message = "This card checkout expired or failed. Please choose the rent periods again." });
                }

                return Ok(BuildRentCheckoutSessionDto(
                    payment,
                    payment.Tenancy,
                    periods,
                    chargeAmount: ConvertXafToStripeMinorUnits(payment.Amount) / 100m,
                    chargeCurrency: ResolveStripeCurrency(),
                    clientSecret: remoteStatus.ClientSecret,
                    publishableKey: ResolveStripePublishableKey(),
                    providerReference: remoteStatus.SessionId,
                    returnUrl: BuildRentPaymentCallbackUrl(payment.RequestKey, remoteStatus.SessionId)));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("rent-periods/checkout-status/{reference}")]
        [Authorize]
        public async Task<IActionResult> GetRentCheckoutStatus(string reference)
        {
            try
            {
                var currentUserId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Unauthorized();
                }

                var payment = await _context.Payments
                    .Include(p => p.Tenancy)
                    .ThenInclude(t => t!.Apartment)
                    .ThenInclude(a => a!.Property)
                    .FirstOrDefaultAsync(p =>
                        p.RequestKey == reference &&
                        p.TenantId == currentUserId &&
                        p.Method == PaymentMethodEnum.Card &&
                        !p.IsDeleted);

                if (payment == null)
                {
                    return NotFound("Rent card checkout was not found.");
                }

                if (payment.Status == PaymentStatusEnum.Pending &&
                    IsStripeCheckoutSessionId(payment.TransactionId))
                {
                    var remoteStatus = await _stripeCheckoutService.RetrieveSessionAsync(payment.TransactionId);
                    if (remoteStatus != null &&
                        (string.IsNullOrWhiteSpace(remoteStatus.PaymentReference) ||
                         string.Equals(remoteStatus.PaymentReference, payment.RequestKey, StringComparison.Ordinal)))
                    {
                        await ApplyRentCheckoutStatusAsync(payment, remoteStatus, currentUserId);
                    }
                }

                var completed = payment.Status == PaymentStatusEnum.Success;
                return Ok(new RentCheckoutStatusDto
                {
                    PaymentId = payment.Id,
                    TenancyId = payment.TenancyId ?? 0,
                    PaymentReference = payment.RequestKey,
                    ProviderReference = payment.TransactionId,
                    PaymentStatus = payment.Status.ToString(),
                    PaymentCompleted = completed,
                    ReceiptNumber = payment.SystemReceiptNumber ?? string.Empty,
                    Message = completed
                        ? "Rent payment completed successfully."
                        : payment.Status == PaymentStatusEnum.Failed
                            ? "The card payment was not completed. Please choose the rent periods and try again."
                            : payment.Status == PaymentStatusEnum.Cancelled
                                ? "The card payment was cancelled. Please choose the rent periods again."
                            : "The card payment is still pending. Please complete the secure card form."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("rent-periods/{rentPeriodId:int}/mark-paid")]
        [Authorize]
        public async Task<IActionResult> MarkRentPeriodAsPaid(int rentPeriodId, [FromBody] MarkRentPeriodPaidRequest? request)
        {
            return await MarkRentPeriodsAsPaidCoreAsync(
                new[] { rentPeriodId },
                request?.PaidDate,
                request?.Note);
        }

        [HttpPost("rent-periods/mark-paid")]
        [Authorize]
        public async Task<IActionResult> MarkRentPeriodsAsPaid([FromBody] MarkRentPeriodsPaidRequest request)
        {
            return await MarkRentPeriodsAsPaidCoreAsync(
                request.RentPeriodIds,
                request.PaidDate,
                request.Note);
        }

        private async Task<IActionResult> MarkRentPeriodsAsPaidCoreAsync(
            IEnumerable<int> requestedPeriodIds,
            DateTimeOffset? requestedPaidDate,
            string? note)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var periodIds = requestedPeriodIds.Distinct().ToList();
                if (periodIds.Count == 0)
                {
                    return BadRequest("At least one rent period must be selected.");
                }

                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var selectedPeriods = await _context.RentPeriods
                    .Where(period => periodIds.Contains(period.Id) && !period.IsDeleted)
                    .OrderBy(period => period.PeriodStart)
                    .ToListAsync();
                if (selectedPeriods.Count != periodIds.Count)
                {
                    return NotFound("One or more rent periods were not found.");
                }

                var tenancyId = selectedPeriods[0].TenancyId;
                if (selectedPeriods.Any(period => period.TenancyId != tenancyId))
                {
                    return BadRequest("All selected rent periods must belong to the same tenancy.");
                }

                var tenancy = await _context.Tenancies
                    .Include(item => item.Apartment)
                    .ThenInclude(apartment => apartment!.Property)
                    .Include(item => item.Members)
                    .ThenInclude(member => member.Member)
                    .FirstOrDefaultAsync(item => item.Id == tenancyId && !item.IsDeleted);
                if (tenancy?.Apartment?.Property == null)
                {
                    return NotFound("Tenancy or property not found.");
                }

                if (!await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancy.Id, ManagerPermission.MarkRentAsPaid, User.IsInRole("Admin")))
                {
                    return Forbid();
                }

                var selectedIds = selectedPeriods.Select(period => period.Id).ToList();
                var requestKey = $"manual-rent-periods:{string.Join(',', selectedIds)}";
                var existingPayment = await _context.Payments
                    .FirstOrDefaultAsync(payment => payment.RequestKey == requestKey && !payment.IsDeleted);
                if (existingPayment != null)
                {
                    await transaction.CommitAsync();
                    var existingReceipt = await _receiptService.EnsureReceiptAsync(existingPayment.Id, userId);
                    return Ok(new
                    {
                        existingPayment.Id,
                        Status = existingPayment.Status.ToString(),
                        existingPayment.TransactionId,
                        ReceiptNumber = existingReceipt?.ReceiptNumber ?? string.Empty,
                        Duplicate = true
                    });
                }

                if (selectedPeriods.Any(period => RentPeriodScheduleHelper.IsPaidStatus(period.Status)))
                {
                    return BadRequest("One or more selected rent periods are already paid or closed.");
                }

                if (selectedPeriods.Any(period => period.Status == RentPeriodStatusEnum.PendingPayment))
                {
                    return BadRequest("One or more selected rent periods already have a pending payment.");
                }

                var tenancyPeriods = await _context.RentPeriods
                    .Where(rp => rp.TenancyId == tenancy.Id && !rp.IsDeleted)
                    .OrderBy(rp => rp.PeriodStart)
                    .ToListAsync();

                var payableInOrder = tenancyPeriods.Where(rp =>
                    !RentPeriodScheduleHelper.IsPaidStatus(rp.Status) &&
                    rp.Status != RentPeriodStatusEnum.PendingPayment).ToList();
                var expectedIds = payableInOrder
                    .Take(selectedPeriods.Count)
                    .Select(period => period.Id)
                    .ToList();
                if (!expectedIds.SequenceEqual(selectedIds))
                {
                    return BadRequest("Selected rent periods must be consecutive and start with the oldest unpaid period.");
                }

                var mainTenant = tenancy.Members
                    .Where(member => !member.IsDeleted)
                    .OrderBy(member => member.Role == TenancyMemberRoleEnum.MainTenant ? 0 : 1)
                    .ThenBy(member => member.CreatedAt)
                    .FirstOrDefault();

                if (mainTenant == null)
                {
                    return BadRequest("A tenant member is required before recording a cash rent payment.");
                }

                var amountDue = selectedPeriods.Sum(period => Math.Max(0, period.Amount - period.PaidAmount));
                if (amountDue <= 0)
                {
                    return BadRequest("The selected rent periods do not have a positive balance.");
                }

                var paidDate = requestedPaidDate ?? DateTimeOffset.UtcNow;
                var payment = new Payment
                {
                    TenantId = mainTenant.MemberId,
                    LandlordId = tenancy.Apartment.Property.LandlordId,
                    TenancyId = tenancy.Id,
                    Amount = amountDue,
                    Currency = "XAF",
                    Method = PaymentMethodEnum.Cash,
                    RequestKey = requestKey,
                    TransactionId = $"manual-{Guid.NewGuid():N}",
                    Status = PaymentStatusEnum.Success,
                    PaymentDate = paidDate,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();

                foreach (var period in selectedPeriods)
                {
                    period.Status = RentPeriodStatusEnum.Paid;
                    period.PaidAmount = period.Amount;
                    period.PaidDate = paidDate;
                    period.PaymentId = payment.Id;
                    period.PaymentReference = payment.TransactionId;
                    period.UpdatedBy = userId;
                    period.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                var receipt = await _receiptService.EnsureReceiptAsync(payment.Id, userId);
                if (receipt != null)
                {
                    await _receiptService.SendReceiptNotificationsAsync(
                        receipt,
                        notifyTenant: true,
                        notifyLandlord: !string.Equals(payment.LandlordId, userId, StringComparison.Ordinal));
                }

                return Ok(new
                {
                    payment.Id,
                    Status = payment.Status.ToString(),
                    payment.TransactionId,
                    ReceiptNumber = receipt?.ReceiptNumber ?? string.Empty,
                    CoveredPeriodIds = selectedIds,
                    Note = string.IsNullOrWhiteSpace(note) ? string.Empty : note.Trim(),
                    Duplicate = false
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("rent-periods/{rentPeriodId:int}/cancel-pending-payment")]
        [Authorize]
        public async Task<IActionResult> CancelPendingRentPayment(int rentPeriodId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var period = await _context.RentPeriods
                    .Include(rp => rp.Payment)
                    .Include(rp => rp.Tenancy)
                    .ThenInclude(t => t!.Apartment)
                    .ThenInclude(a => a!.Property)
                    .FirstOrDefaultAsync(rp => rp.Id == rentPeriodId && !rp.IsDeleted);

                if (period == null)
                {
                    return NotFound("Rent period not found.");
                }

                var tenancy = period.Tenancy;
                if (tenancy?.Apartment?.Property == null)
                {
                    return NotFound("Tenancy or property not found.");
                }

                if (!await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancy.Id, ManagerPermission.DeletePayment, User.IsInRole("Admin")))
                {
                    return Forbid();
                }

                if (period.Status != RentPeriodStatusEnum.PendingPayment ||
                    !period.PaymentId.HasValue ||
                    period.Payment == null)
                {
                    return BadRequest("This rent period does not have a pending payment to cancel.");
                }

                if (period.Payment.Status != PaymentStatusEnum.Pending)
                {
                    return BadRequest("Only a pending payment can be cancelled.");
                }

                var payment = period.Payment;

                var tenancyPeriods = await _context.RentPeriods
                    .Where(rp => rp.TenancyId == tenancy.Id && !rp.IsDeleted)
                    .OrderBy(rp => rp.PeriodStart)
                    .ToListAsync();

                var firstOpenPeriod = tenancyPeriods.FirstOrDefault(rp =>
                    !RentPeriodScheduleHelper.IsPaidStatus(rp.Status));

                if (firstOpenPeriod == null || firstOpenPeriod.Id != period.Id)
                {
                    return BadRequest("Pending payments must be cancelled in rent period order.");
                }

                var linkedPeriods = tenancyPeriods
                    .Where(rp => rp.PaymentId == period.PaymentId.Value)
                    .ToList();

                if (linkedPeriods.Count == 0 ||
                    linkedPeriods.Any(rp => rp.Status != RentPeriodStatusEnum.PendingPayment))
                {
                    return Conflict(new { Message = "This payment can no longer be cancelled because one of its rent periods has changed." });
                }

                var nowUtc = DateTimeOffset.UtcNow;
                payment.Status = PaymentStatusEnum.Cancelled;
                payment.RequestKey = $"cancelled:{payment.Id}:{payment.RequestKey}";
                payment.UpdatedBy = userId;
                payment.UpdatedAt = nowUtc;

                foreach (var linkedPeriod in linkedPeriods)
                {
                    linkedPeriod.Status = RentPeriodScheduleHelper.ResolveUnpaidStatus(linkedPeriod.DueDate, nowUtc);
                    linkedPeriod.PaymentId = null;
                    linkedPeriod.PaymentReference = string.Empty;
                    linkedPeriod.UpdatedBy = userId;
                    linkedPeriod.UpdatedAt = nowUtc;
                }

                await _context.SaveChangesAsync();

                return Ok(new
                {
                    Message = "Pending rent payment cancelled.",
                    PaymentId = payment.Id,
                    ReleasedPeriodIds = linkedPeriods.Select(rp => rp.Id).ToList()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Marks a payment as paid (success) manually.  Only the landlord of the payment or an
        /// authorized manager/owner with write permission may perform this action.  Useful if
        /// the tenant cannot complete the payment through the system.
        /// </summary>
        [HttpPut("{id}/mark-paid")]
        [Authorize]
        public async Task<IActionResult> MarkPaymentAsPaid(int id)
        {
            try
            {
                var payment = await _context.Payments
                    .Include(p => p.Tenancy)
                    .ThenInclude(t => t.Apartment)
                    .FirstOrDefaultAsync(p => p.Id == id);
                if (payment == null) return NotFound("Payment not found.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Determine if user can mark payment as paid
                bool isLandlord = payment.LandlordId == userId;
                bool canWrite = false;
                if (isLandlord)
                {
                    canWrite = true;
                }
                else if (payment.Tenancy != null)
                {
                    var tenancy = payment.Tenancy;
                    // Owner with write permission on the apartment
                    var ownerWrite = await _context.ApartmentOwners
                        .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    var managerWrite = await _permissionService.HasTenancyPermissionAsync(
                        userId,
                        tenancy.Id,
                        ManagerPermission.MarkRentAsPaid,
                        User.IsInRole("Admin"));
                    canWrite = ownerWrite || managerWrite;
                }
                if (!canWrite)
                {
                    return Forbid();
                }
                if (payment.Status == PaymentStatusEnum.Success)
                {
                    return BadRequest("Payment is already marked as paid.");
                }
                payment.Status = PaymentStatusEnum.Success;
                payment.UpdatedBy = userId;
                payment.UpdatedAt = DateTime.UtcNow;
                _context.Payments.Update(payment);
                await _context.SaveChangesAsync();
                var receipt = await _receiptService.EnsureReceiptAsync(payment.Id, userId);
                if (receipt != null)
                {
                    await _receiptService.SendReceiptNotificationsAsync(
                        receipt,
                        notifyTenant: true,
                        notifyLandlord: !string.Equals(payment.LandlordId, userId, StringComparison.Ordinal));
                }

                return Ok(new { Message = "Payment marked as paid.", PaymentId = payment.Id, ReceiptNumber = receipt?.ReceiptNumber ?? string.Empty });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private static string BuildPaymentRequestKey(
            PaymentRequest request,
            string currentUserId,
            int? tenancyId,
            decimal finalAmount)
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                return HashKey($"client|{currentUserId}|{request.IdempotencyKey.Trim()}");
            }

            var fiveMinuteBucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
            var rawKey = string.Join(
                "|",
                "server",
                currentUserId,
                request.TenantId,
                request.LandlordId,
                tenancyId?.ToString(CultureInfo.InvariantCulture) ?? "none",
                request.Method.ToString(),
                finalAmount.ToString("0.00", CultureInfo.InvariantCulture),
                request.NumberOfPeriods.ToString(CultureInfo.InvariantCulture),
                fiveMinuteBucket.ToString(CultureInfo.InvariantCulture));

            return HashKey(rawKey);
        }

        private static string HashKey(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string BuildPaymentMethodMismatchMessage(PaymentMethodEnum expectedMethod)
        {
            return expectedMethod == PaymentMethodEnum.Card
                ? "Card payment mode is required for this tenancy."
                : $"This tenancy must be paid with {PaymentMethodLabel(expectedMethod)}.";
        }

        private static string PaymentMethodLabel(PaymentMethodEnum method)
        {
            return method switch
            {
                PaymentMethodEnum.Momo => "MTN Mobile Money",
                PaymentMethodEnum.OrangeMoney => "Orange Money",
                PaymentMethodEnum.Card => "card payment",
                PaymentMethodEnum.Cash => "cash",
                _ => method.ToString()
            };
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            if (ex.GetBaseException() is SqlException sqlException)
            {
                return sqlException.Number is 2601 or 2627;
            }

            var message = ex.GetBaseException().Message;
            return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
        }

        private async Task ApplyRentCheckoutStatusAsync(
            Payment payment,
            StripeCheckoutStatus remoteStatus,
            string actorId)
        {
            if (payment.Status == PaymentStatusEnum.Cancelled)
            {
                return;
            }

            var providerStatus = ResolveStripeProviderStatus(remoteStatus.PaymentStatus, remoteStatus.Status);
            var periods = await _context.RentPeriods
                .Where(period => period.PaymentId == payment.Id && !period.IsDeleted)
                .OrderBy(period => period.PeriodStart)
                .ToListAsync();

            if (IsSuccessfulProviderStatus(providerStatus))
            {
                var wasAlreadySuccessful = payment.Status == PaymentStatusEnum.Success;
                payment.Status = PaymentStatusEnum.Success;
                payment.PaymentDate = DateTimeOffset.UtcNow;
                payment.ProviderReceiptUrl = string.IsNullOrWhiteSpace(remoteStatus.ProviderReceiptUrl)
                    ? payment.ProviderReceiptUrl
                    : remoteStatus.ProviderReceiptUrl;
                payment.UpdatedBy = actorId;
                payment.UpdatedAt = DateTimeOffset.UtcNow;

                foreach (var period in periods)
                {
                    period.Status = RentPeriodStatusEnum.Paid;
                    period.PaidAmount = period.Amount;
                    period.PaidDate = payment.PaymentDate;
                    period.PaymentReference = string.IsNullOrWhiteSpace(remoteStatus.PaymentIntentId)
                        ? remoteStatus.SessionId
                        : remoteStatus.PaymentIntentId;
                    period.UpdatedBy = actorId;
                    period.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await _context.SaveChangesAsync();

                var receipt = await _receiptService.EnsureReceiptAsync(payment.Id, actorId);
                if (receipt != null && !wasAlreadySuccessful)
                {
                    await _receiptService.SendReceiptNotificationsAsync(
                        receipt,
                        notifyTenant: true,
                        notifyLandlord: true);
                }

                return;
            }

            if (IsFailedProviderStatus(providerStatus))
            {
                payment.Status = PaymentStatusEnum.Failed;
                payment.ProviderReceiptUrl = string.IsNullOrWhiteSpace(remoteStatus.ProviderReceiptUrl)
                    ? payment.ProviderReceiptUrl
                    : remoteStatus.ProviderReceiptUrl;
                payment.UpdatedBy = actorId;
                payment.UpdatedAt = DateTimeOffset.UtcNow;

                var nowUtc = DateTimeOffset.UtcNow;
                foreach (var period in periods.Where(period => period.Status == RentPeriodStatusEnum.PendingPayment))
                {
                    period.Status = RentPeriodScheduleHelper.ResolveUnpaidStatus(period.DueDate, nowUtc);
                    period.PaymentId = null;
                    period.PaymentReference = string.Empty;
                    period.UpdatedBy = actorId;
                    period.UpdatedAt = nowUtc;
                }

                await _context.SaveChangesAsync();
            }
        }

        private RentCheckoutSessionDto BuildRentCheckoutSessionDto(
            Payment payment,
            Tenancy? tenancy,
            IReadOnlyCollection<RentPeriod> periods,
            decimal chargeAmount,
            string chargeCurrency,
            string clientSecret,
            string publishableKey,
            string providerReference,
            string returnUrl = "")
        {
            return new RentCheckoutSessionDto
            {
                PaymentId = payment.Id,
                TenancyId = payment.TenancyId ?? tenancy?.Id ?? 0,
                RentAmount = payment.Amount,
                RentCurrency = payment.Currency,
                ChargeAmount = chargeAmount,
                ChargeCurrency = string.IsNullOrWhiteSpace(chargeCurrency) ? "USD" : chargeCurrency,
                PaymentMethod = PaymentMethodEnum.Card,
                PaymentReference = payment.RequestKey,
                ProviderReference = providerReference,
                ClientSecret = clientSecret,
                PublishableKey = publishableKey,
                ReturnUrl = string.IsNullOrWhiteSpace(returnUrl)
                    ? BuildRentPaymentCallbackUrl(payment.RequestKey, providerReference)
                    : returnUrl,
                CheckoutUrl = BuildRentCardCheckoutUrl(payment.RequestKey),
                Provider = "Stripe",
                PropertyName = tenancy?.Apartment?.Property?.Name ?? string.Empty,
                ApartmentName = tenancy?.Apartment?.Name ?? string.Empty,
                PeriodLabel = BuildPeriodLabel(periods),
                Status = payment.Status.ToString()
            };
        }

        private string BuildRentCardCheckoutUrl(string paymentReference)
        {
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                throw new InvalidOperationException("Portal base URL is missing.");
            }

            return $"{portalBaseUrl}/Tenant/RentCardCheckout?reference={Uri.EscapeDataString(paymentReference)}";
        }

        private string BuildRentPaymentCallbackUrl(string paymentReference, string? providerReference)
        {
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                throw new InvalidOperationException("Portal base URL is missing.");
            }

            var url = $"{portalBaseUrl}/Tenant/RentPaymentCallback?reference={Uri.EscapeDataString(paymentReference)}";
            return string.IsNullOrWhiteSpace(providerReference)
                ? url
                : $"{url}&session_id={Uri.EscapeDataString(providerReference)}";
        }

        private string ResolveStripePublishableKey()
        {
            return _configuration["Stripe:PublishableKey"]?.Trim() ?? string.Empty;
        }

        private string ResolveStripeCurrency()
        {
            var configured = (_configuration["Stripe:Currency"] ?? "usd").Trim();
            return string.IsNullOrWhiteSpace(configured)
                ? "USD"
                : configured.ToUpperInvariant();
        }

        private long ConvertXafToStripeMinorUnits(decimal xafAmount)
        {
            var usdToXafRate = _configuration.GetValue<decimal?>("Subscriptions:UsdToXafRate") ?? 565m;
            if (usdToXafRate <= 0)
            {
                usdToXafRate = 565m;
            }

            var checkoutAmount = xafAmount / usdToXafRate;
            var minorUnits = decimal.Round(checkoutAmount * 100m, 0, MidpointRounding.AwayFromZero);
            return Math.Max(50, decimal.ToInt64(minorUnits));
        }

        private static bool IsStripeCheckoutSessionId(string? value)
        {
            return (value ?? string.Empty).Trim().StartsWith("cs_", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveStripeProviderStatus(string? paymentStatus, string? sessionStatus)
        {
            var normalizedPaymentStatus = (paymentStatus ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalizedPaymentStatus) &&
                normalizedPaymentStatus != "unpaid")
            {
                return normalizedPaymentStatus;
            }

            return (sessionStatus ?? normalizedPaymentStatus ?? "open").Trim().ToLowerInvariant();
        }

        private static bool IsSuccessfulProviderStatus(string providerStatus)
        {
            var normalizedStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedStatus is "complete" or "success" or "successful" or "succeeded" or "paid";
        }

        private static bool IsFailedProviderStatus(string providerStatus)
        {
            var normalizedStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedStatus is "failed" or "canceled" or "cancelled" or "expired";
        }

        private static string BuildPeriodLabel(IReadOnlyCollection<RentPeriod> periods)
        {
            if (periods.Count == 0)
            {
                return "Rent payment";
            }

            var ordered = periods.OrderBy(period => period.PeriodStart).ToList();
            var first = ordered.First();
            var last = ordered.Last();
            return ordered.Count == 1
                ? $"{first.PeriodStart:MMM d, yyyy} - {first.PeriodEnd:MMM d, yyyy}"
                : $"{first.PeriodStart:MMM d, yyyy} - {last.PeriodEnd:MMM d, yyyy}";
        }

    }
}




