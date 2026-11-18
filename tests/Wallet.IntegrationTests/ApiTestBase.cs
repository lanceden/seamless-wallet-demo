using System.Net.Http.Json;
using System.Text.Json;

namespace Wallet.IntegrationTests;

/// <summary>每個測試結束時都跑一次帳務不變式檢查（PLAN §10.1 最後一項）；有違反就讓該測試失敗。</summary>
public abstract class ApiTestBase(ApiFixture fixture) : IAsyncLifetime
{
    protected ApiFixture Api { get; } = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var resp = await Api.Http.GetAsync("/admin/health/invariants");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean(), "帳務不變式被破壞：" + body.GetRawText());
    }
}
