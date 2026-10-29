using Help_Desk.Services;
using Microsoft.AspNetCore.Mvc;
namespace Help_Desk.ViewComponents;
public class TatAlertsViewComponent(TatAlerts alerts) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync() => View(await alerts.ReadAsync(HttpContext.RequestAborted));
}
