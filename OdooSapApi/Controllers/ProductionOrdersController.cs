using Microsoft.AspNetCore.Mvc;
using OdooSapApi.Models;
using OdooSapApi.Services;

namespace OdooSapApi.Controllers;

[ApiController]
[Route("api/sap/production")]
public class ProductionOrdersController : ControllerBase
{
    private readonly ProductionOrderService _productionOrderService;

    public ProductionOrdersController(ProductionOrderService productionOrderService)
    {
        _productionOrderService = productionOrderService;
    }

    [HttpPost("issue")]
    public async Task<ActionResult<ApiResponse>> Issue([FromBody] ProductionIssueRequest request)
    {
        return await ExecuteAsync(() => _productionOrderService.IssueAsync(request));
    }

    [HttpPost("receipt")]
    public async Task<ActionResult<ApiResponse>> Receipt([FromBody] ProductionReceiptRequest request)
    {
        return await ExecuteAsync(() => _productionOrderService.ReceiptAsync(request));
    }

    [HttpPost("close")]
    public async Task<ActionResult<ApiResponse>> Close([FromBody] ProductionCloseRequest request)
    {
        return await ExecuteAsync(() => _productionOrderService.CloseAsync(request));
    }

    private async Task<ActionResult<ApiResponse>> ExecuteAsync(Func<Task<ApiResponse>> action)
    {
        try
        {
            var response = await action();
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiResponse
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
