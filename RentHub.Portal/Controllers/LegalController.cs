using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class LegalController : Controller
    {
        [HttpGet]
        public IActionResult TenancyNotice() => View();
    }
}
