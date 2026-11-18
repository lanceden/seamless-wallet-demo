using System.Net;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DemoPageTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Root_serves_the_demo_page()
    {
        var r = await Api.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/html", r.Content.Headers.ContentType!.MediaType);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("Seamless Wallet Demo", html);
        Assert.Contains("crypto.subtle", html); // 頁面在瀏覽器端用 WebCrypto 做 HMAC 簽章
    }
}
