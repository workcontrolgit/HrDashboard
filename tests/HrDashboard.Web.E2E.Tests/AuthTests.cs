using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace HrDashboard.Web.E2E.Tests;

public class AuthTests : PageTest
{
    private const string Password = "Passw0rd!";

    private static string UniqueEmail() => $"e2e-{Guid.NewGuid():N}@test.local";

    private async Task RegisterAsync(string email, string password)
    {
        await Page.GotoAsync($"{WebAppFixture.BaseUrl}/register");
        await Page.GetByLabel("Email").FillAsync(email);
        await Page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await Page.GetByLabel("Confirm password").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Register" }).ClickAsync();
    }

    private async Task LoginAsync(string email, string password)
    {
        await Page.GotoAsync($"{WebAppFixture.BaseUrl}/login");
        await Page.GetByLabel("Email").FillAsync(email);
        await Page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
    }

    [Test]
    public async Task Register_NewUser_Succeeds()
    {
        var email = UniqueEmail();

        await RegisterAsync(email, Password);

        // Registering signs the user in immediately and redirects to the dashboard root.
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");
    }

    [Test]
    public async Task Login_ValidCredentials_ReachesDashboard()
    {
        var email = UniqueEmail();
        await RegisterAsync(email, Password);
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");

        // Sign out the freshly-registered (auto-signed-in) user, then log back in explicitly.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/login");

        await LoginAsync(email, Password);

        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");
    }

    [Test]
    public async Task Login_InvalidCredentials_ShowsError()
    {
        await LoginAsync(UniqueEmail(), "wrong-password");

        await Expect(Page).ToHaveURLAsync(new Regex(@"/login\?error=invalid"));
        await Expect(Page.GetByText("Invalid email or password.")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Logout_SignedInUser_ReturnsToLogin()
    {
        var email = UniqueEmail();
        await RegisterAsync(email, Password);
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/login");
    }
}
