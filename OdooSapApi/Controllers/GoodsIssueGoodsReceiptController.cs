using Microsoft.AspNetCore.Mvc;
using OdooSapApi.Models;
using OdooSapApi.Services;

namespace OdooSapApi.Controllers;

[ApiController]
[Route("api/sap/goodsissue-goodsreceipt")]
public class GoodsIssueGoodsReceiptController : ControllerBase
{
    private readonly IntercompanyTransferService _transferService;

    public GoodsIssueGoodsReceiptController(
        IntercompanyTransferService transferService)
    {
        _transferService = transferService;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse>> Create(
        [FromBody] IntercompanyTransferRequest request)
    {
        try
        {
            var result = await _transferService.ProcessAsync(
                request,
                HttpContext.RequestAborted);
            return Ok(new ApiResponse
            {
                Success = true,
                Message = "Goods Issue and Goods Receipt completed.",
                Data = result
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Failure(ex.Message));
        }
        catch (IntercompanyTransferConflictException ex)
        {
            return Conflict(Failure(ex.Message, ex.Result));
        }
        catch (IntercompanyTransferBusyException ex)
        {
            return Conflict(Failure(ex.Message));
        }
        catch (SapDiApiBusyException ex)
        {
            Response.Headers.RetryAfter = Convert.ToString(ex.RetryAfterSeconds);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Failure(ex.Message));
        }
        catch (IntercompanyTransferProcessingException ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                Failure(ex.InnerException?.Message ?? ex.Message, ex.Result));
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                Failure(ex.Message));
        }
    }

    [HttpGet("{transferId}")]
    public async Task<ActionResult<ApiResponse>> GetStatus(
        string transferId,
        [FromQuery] string siteId)
    {
        try
        {
            var result = await _transferService.GetStatusAsync(siteId, transferId);

            if (result is null)
            {
                return NotFound(Failure("Transfer was not found."));
            }

            return Ok(new ApiResponse
            {
                Success = string.Equals(
                    result.Status,
                    "COMPLETED",
                    StringComparison.OrdinalIgnoreCase),
                Message = $"Transfer status: {result.Status}.",
                Data = result
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Failure(ex.Message));
        }
        catch (IntercompanyTransferBusyException ex)
        {
            return Conflict(Failure(ex.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                Failure(ex.Message));
        }
    }

    private static ApiResponse Failure(string message, object? data = null)
    {
        return new ApiResponse
        {
            Success = false,
            Message = message,
            Data = data
        };
    }
}
