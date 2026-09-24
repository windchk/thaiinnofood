using Microsoft.AspNetCore.Mvc;
using OdooSapApi.Models;
using OdooSapApi.Services;

namespace OdooSapApi.Controllers;

[ApiController]
[Route("api/sap/delivery")]
public class DeliveryController : ControllerBase
{
    private readonly ProductionOrderService _sapDocumentService;

    public DeliveryController(ProductionOrderService sapDocumentService)
    {
        _sapDocumentService = sapDocumentService;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse>> Create([FromBody] DeliveryRequest request)
    {
        try
        {
            return Ok(await _sapDocumentService.DeliveryAsync(
                request,
                HttpContext.RequestAborted));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiResponse
            {
                Success = false,
                Message = ex.Message
            });
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
