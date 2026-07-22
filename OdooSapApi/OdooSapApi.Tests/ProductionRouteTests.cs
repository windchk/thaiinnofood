using OdooSapApi.Controllers;
using Xunit;

namespace OdooSapApi.Tests;

public class ProductionRouteTests
{
    [Fact]
    public void CloseProductionOrderRoute_IsNotCompiled()
    {
        var controllerType = typeof(ProductionOrdersController);

        Assert.NotNull(controllerType.GetMethod("Issue"));
        Assert.NotNull(controllerType.GetMethod("Receipt"));
        Assert.Null(controllerType.GetMethod("Close"));
    }
}
