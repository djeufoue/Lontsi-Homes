using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Helpers;
using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class AdminTransferAccountsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AdminTransferAccountsController> _logger;

        public AdminTransferAccountsController(
            ApplicationDbContext context,
            ILogger<AdminTransferAccountsController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            await EnsureDefaultAccountsAsync();

            var accounts = await _context.SystemTransferAccounts
                .OrderBy(a => a.Channel)
                .Select(a => new SystemTransferAccountDto
                {
                    Id = a.Id,
                    Channel = a.Channel,
                    ChannelLabel = a.Channel == PayoutChannelEnum.MtnMoney ? "MTN Money" : "Orange Money",
                    AccountName = a.AccountName,
                    PhoneNumber = a.PhoneNumber,
                    CountryCode = a.CountryCode,
                    Notes = a.Notes,
                    IsConfigured = !string.IsNullOrWhiteSpace(a.AccountName) && !string.IsNullOrWhiteSpace(a.PhoneNumber),
                    UpdatedAt = a.UpdatedAt
                })
                .ToListAsync();

            return Ok(accounts);
        }

        [HttpPut("{channel}")]
        public async Task<IActionResult> Upsert(PayoutChannelEnum channel, [FromBody] UpsertSystemTransferAccountRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                if (channel != request.Channel)
                {
                    return BadRequest("Channel mismatch.");
                }

                if (channel != PayoutChannelEnum.MtnMoney && channel != PayoutChannelEnum.OrangeMoney)
                {
                    return BadRequest("Only MTN Money and Orange Money are supported here.");
                }

                await EnsureDefaultAccountsAsync();

                var userId = UserHelpers.GetUserId(User);
                var account = await _context.SystemTransferAccounts.FirstOrDefaultAsync(a => a.Channel == channel);
                if (account == null)
                {
                    account = new SystemTransferAccount
                    {
                        Channel = channel,
                        CreatedAt = DateTimeOffset.UtcNow,
                        CreatedBy = userId
                    };

                    _context.SystemTransferAccounts.Add(account);
                }

                account.AccountName = request.AccountName.Trim();
                account.PhoneNumber = PhoneNumberHelper.NormalizeOrEmpty(request.PhoneNumber);
                account.CountryCode = PhoneNumberHelper.NormalizeOrEmpty(request.CountryCode);
                account.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
                account.UpdatedAt = DateTimeOffset.UtcNow;
                account.UpdatedBy = userId;

                await _context.SaveChangesAsync();

                return Ok(new { Message = $"{(channel == PayoutChannelEnum.MtnMoney ? "MTN Money" : "Orange Money")} receiving account saved successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save admin transfer account for channel {Channel}", channel);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "ADMIN_TRANSFER_ACCOUNT_SAVE_FAILED",
                    Message = "Unable to save the receiving account right now. Please try again."
                });
            }
        }

        private async Task EnsureDefaultAccountsAsync()
        {
            var channels = new[] { PayoutChannelEnum.MtnMoney, PayoutChannelEnum.OrangeMoney };
            var existing = await _context.SystemTransferAccounts
                .Where(a => channels.Contains(a.Channel))
                .Select(a => a.Channel)
                .ToListAsync();

            var missing = channels.Except(existing).ToList();
            if (!missing.Any())
            {
                return;
            }

            foreach (var channel in missing)
            {
                _context.SystemTransferAccounts.Add(new SystemTransferAccount
                {
                    Channel = channel,
                    AccountName = string.Empty,
                    PhoneNumber = string.Empty,
                    CountryCode = "+237",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

            await _context.SaveChangesAsync();
        }
    }
}
