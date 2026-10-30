using Microsoft.AspNetCore.Mvc;
namespace StokvelSync.Web.Controllers;
public sealed class HomeController : Controller
{
    [HttpGet("/")]
    [HttpGet("/demo")]
    public IActionResult Index() { ViewData["Demo"] = Request.Path == "/demo" ? "true" : "false"; return View(); }
}
