using System;
using System.Threading.Tasks;
using JioTv.Plugin.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CS1591

namespace JioTv.Plugin.Configuration;

/// <summary>
/// Dashboard-facing endpoints backing the plugin config page login flow:
/// status (account + token expiry), OTP send/verify, logout.
/// </summary>
[ApiController]
[Authorize]
public sealed class JioTvAuthController : ControllerBase
{
    private readonly JioAuth _auth;
    private readonly CredentialStore _store;

    public JioTvAuthController(JioAuth auth, CredentialStore store)
    {
        _auth = auth;
        _store = store;
    }

    /// <summary>Current account state for the config page.</summary>
    [HttpGet("/JioTv/Auth/Status")]
    public IActionResult Status()
    {
        var creds = _store.Load();
        if (creds is null || string.IsNullOrEmpty(creds.AccessToken))
        {
            return Ok(new { loggedIn = false });
        }

        var expiry = string.Empty;
        var msToExpiry = 0L;
        if (Auth.TokenValidity.TryParseJwtExpiry(creds.AccessToken, out var exp))
        {
            expiry = exp.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
            msToExpiry = (long)(exp - DateTime.UtcNow).TotalMilliseconds;
        }

        return Ok(new
        {
            loggedIn = true,
            name = creds.MobileNumber,
            crm = creds.CRM,
            expiry,
            msToExpiry,
            deviceId = creds.DeviceId,
        });
    }

    /// <summary>Sends an OTP to the given 10-digit mobile number.</summary>
    [HttpPost("/JioTv/Auth/SendOtp")]
    public async Task<IActionResult> SendOtp([FromForm] string mobileNumber)
    {
        if (string.IsNullOrWhiteSpace(mobileNumber) || mobileNumber.Length < 10)
        {
            return BadRequest(new { message = "Enter a valid 10-digit mobile number" });
        }

        try
        {
            await _auth.SendOtpAsync(mobileNumber).ConfigureAwait(false);
            return Ok(new { status = "success" });
        }
        catch (ExternalApiException ex)
        {
            return Ok(new { status = "failed", message = ex.Message });
        }
        catch (Exception)
        {
            return Ok(new { status = "failed", message = "OTP send failed; check server logs" });
        }
    }

    /// <summary>Verifies the OTP and stores credentials.</summary>
    [HttpPost("/JioTv/Auth/VerifyOtp")]
    public async Task<IActionResult> VerifyOtp([FromForm] string mobileNumber, [FromForm] string otp)
    {
        if (string.IsNullOrWhiteSpace(otp))
        {
            return BadRequest(new { message = "Enter the OTP" });
        }

        var result = await _auth.VerifyOtpAsync(mobileNumber, otp).ConfigureAwait(false);
        return Ok(new
        {
            status = result.Status,
            message = result.Message,
        });
    }

    /// <summary>Clears stored credentials (logout).</summary>
    [HttpPost("/JioTv/Auth/Logout")]
    public IActionResult Logout()
    {
        _store.Clear();
        return Ok(new { status = "success" });
    }
}
