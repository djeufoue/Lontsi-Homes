using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LontsiHomes.Portal.Controllers
{
    [Authorize]
    public class LegalController : Controller
    {
        [HttpGet]
        public IActionResult TenancyNotice() => View();
    }
}
