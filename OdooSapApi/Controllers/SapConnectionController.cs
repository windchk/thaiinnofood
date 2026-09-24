using Microsoft.AspNetCore.Mvc;
using OdooSapApi.Models;
using OdooSapApi.Services;

namespace OdooSapApi.Controllers;

[ApiController]
[Route("api/sap/connection")]
public class SapConnectionController : ControllerBase
{
    private readonly ISapProductionService _sapProductionService;

    public SapConnectionController(ISapProductionService sapProductionService)
    {
        _sapProductionService = sapProductionService;
    }

    [HttpGet("check")]
    public async Task<ActionResult<ApiResponse>> Check([FromQuery] string? siteId = null)
    {
        try
        {
            var response = await _sapProductionService.CheckConnectionAsync(
                siteId,
                HttpContext.RequestAborted);
            return Ok(response);
        }
        catch (SapDiApiBusyException ex)
        {
            Response.Headers.RetryAfter = Convert.ToString(ex.RetryAfterSeconds);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiResponse
            {
                Success = false,
                Message = ex.Message
            });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiResponse
            {
                Success = false,
                Message = ex.Message
            });
        }
    }
}
