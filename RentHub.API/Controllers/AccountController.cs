using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Auth;
using System.Security.Claims;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AccountController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly TokenService _tokenService;

        public AccountController(UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager,
            ApplicationDbContext context,
            TokenService tokenService)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _tokenService = tokenService;
        }

        /// <summary>
        /// Registers a new user.  All self-registered accounts are considered landlords.  A subscription
        /// plan must be specified and will remain pending until approved by an administrator.  Other
        /// roles (Owner, Manager, Tenant) must be created by a landlord via dedicated endpoints.
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                // Self-registration is allowed only for landlords. Other account types are created via separate endpoints.
                if (!request.PlanId.HasValue)
                {
                    return BadRequest("Please choose a subscription plan to complete landlord registration.");
                }

                // Check if user already exists (by email)
                var existingUser = await _userManager.FindByEmailAsync(request.Email);
                if (existingUser != null)
                {
                    return BadRequest("An account with this email already exists. Please log in instead.");
                }
                // Validate the selected plan before creating user records.
                var plan = await _context.SubscriptionPlans.FindAsync(request.PlanId.Value);
                if (plan == null) return BadRequest("Invalid subscription plan.");

                var user = new ApplicationUser
                {
                    UserName = request.Email,
                    Email = request.Email,
                    FullName = request.FullName,
                    CountryCode = request.CountryCode
                };

                var result = await _userManager.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                {
                    return BadRequest(result.Errors);
                }

                // Assign landlord role and fail fast if this step fails.
                var roleResult = await _userManager.AddToRoleAsync(user, "Landlord");
                if (!roleResult.Succeeded)
                {
                    await _userManager.DeleteAsync(user);
                    return BadRequest(roleResult.Errors);
                }

                // Register the user for the selected subscription plan. The subscription will need to be
                // approved by an administrator before the landlord can add owners, managers or tenants.

                var subscription = new UserSubscription
                {
                    UserId = user.Id,
                    SubscriptionPlanId = plan.Id,
                    StartDate = DateTimeOffset.UtcNow,
                    EndDate = DateTimeOffset.UtcNow.AddDays(plan.DurationInDays),
                    IsApproved = false,
                    CreatedBy = user.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.UserSubscriptions.Add(subscription);
                await _context.SaveChangesAsync();

                var token = await _tokenService.GenerateTokenAsync(user);
                return Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Authenticates a user and returns a JWT token on success.
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);
                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null) return Unauthorized("Invalid email or password.");
                var valid = await _userManager.CheckPasswordAsync(user, request.Password);
                if (!valid) return Unauthorized("Invalid email or password.");
                var token = await _tokenService.GenerateTokenAsync(user);
                return Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Changes the password of the current user.
        /// </summary>
        [HttpPost("change-password")]
        [Authorize]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) return Unauthorized();
                var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
                if (!result.Succeeded) return BadRequest(result.Errors);
                return Ok(new { Message = "Password changed successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Generates a password reset token and returns it.  In production this token should be
        /// emailed to the user.
        /// </summary>
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            try
            {
                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null) return Ok(new { Message = "If the email exists, a reset token has been generated." });
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                // TODO: send token via email using EmailService
                return Ok(new { Token = token });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Resets the password using a token obtained from ForgotPassword.
        /// </summary>
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            try
            {
                var user = await _userManager.FindByEmailAsync(request.Email);
                if (user == null) return BadRequest("Invalid email.");
                var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
                if (!result.Succeeded) return BadRequest(result.Errors);
                return Ok(new { Message = "Password has been reset successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Logs the user out.  With JWT this is typically handled on the client by discarding the token.
        /// </summary>
        [HttpPost("logout")]
        [Authorize]
        public IActionResult Logout()
        {
            try
            {
                // For JWT, logout is a client-side operation.  Server can revoke tokens using a blacklist if needed.
                return Ok(new { Message = "Logged out." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}
