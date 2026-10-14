using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FT_MONITOR.Services;

namespace FT_MONITOR.Controllers;

[Authorize]
[ApiController]
[Route("/api/auth")]
public sealed class LocalAuthenticationController(IConfiguration configuration) : ControllerBase
{
    [HttpPost("local-sign-out")]
    public async Task<IActionResult> SignOutLocal()
    {
        if (WebAuthenticationModeConfiguration.GetMode(configuration) !=
            WebAuthenticationMode.LocalDevelopment)
            return NotFound();
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { signedOut = true });
    }
}
