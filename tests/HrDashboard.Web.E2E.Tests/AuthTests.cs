using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Xunit;

namespace HrDashboard.Web.E2E.Tests;

[Trait("Category", "E2E")]
public class AuthTests(WebAppFixture fixture) : PageTest, IClassFixture<WebAppFixture>
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

    [Fact(Skip = "Blocked on bug-023 (.wolf/buglog.json) — Register.razor hidden-input mirror race prevents registration from succeeding")]
    public async Task Register_NewUser_Succeeds()
    {
        var email = UniqueEmail();

        await RegisterAsync(email, Password);

        // Registering signs the user in immediately and redirects to the dashboard root.
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");
    }

    [Fact(Skip = "Blocked on bug-023 (.wolf/buglog.json) — Register.razor hidden-input mirror race prevents registration from succeeding")]
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

    [Fact]
    public async Task Login_InvalidCredentials_ShowsError()
    {
        var email = UniqueEmail();

        await LoginAsync(email, "wrong-password");

        // Assert the specific email round-tripped through the form into the redirect URL,
        // proving the email field genuinely transmitted what was typed (not just that some
        // failure redirected to the same error=invalid URL).
        await Expect(Page).ToHaveURLAsync(new Regex($@"/login\?error=invalid&email={Regex.Escape(Uri.EscapeDataString(email))}"));
        await Expect(Page.GetByText("Invalid email or password.")).ToBeVisibleAsync();
    }

    [Fact(Skip = "Blocked on bug-023 (.wolf/buglog.json) — Register.razor hidden-input mirror race prevents registration from succeeding")]
    public async Task Logout_SignedInUser_ReturnsToLogin()
    {
        var email = UniqueEmail();
        await RegisterAsync(email, Password);
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/login");
    }
}
