using Microsoft.AspNetCore.Mvc;
using OdooSapApi.Models;
using OdooSapApi.Services;

namespace OdooSapApi.Controllers;

[ApiController]
[Route("api/sap/production-orders")]
public class ProductionOrdersController : ControllerBase
{
    private readonly ProductionOrderService _productionOrderService;

    public ProductionOrdersController(ProductionOrderService productionOrderService)
    {
        _productionOrderService = productionOrderService;
    }

    [HttpPost("complete")]
    public async Task<ActionResult<ApiResponse>> Complete([FromBody] ProductionOrderCompleteRequest request)
    {
        try
        {
            var response = await _productionOrderService.CompleteAsync(request);
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
