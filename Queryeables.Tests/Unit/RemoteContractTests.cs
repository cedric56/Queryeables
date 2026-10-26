using Queryeables.Abstraction;
using Queryeables.Onion;
using System.Security.Claims;

namespace Queryeables.Tests.Unit;

/// <summary>Access policies, error codes and versions: what needs neither database nor network.</summary>
public class RemoteContractTests
{
    private static ClaimsPrincipal User(params string[] roles)
        => new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), authenticationType: "test"));

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private static ValueTask<bool> Allowed(IEntityAccessPolicy policy, EntityOperation operation, ClaimsPrincipal? user)
        => policy.IsAllowedAsync(new EntityAccessRequest(operation, typeof(TestClient), null, user));

    // ---------- Access policies ----------

    [Fact]
    public async Task Authenticated_policy_rejects_anonymous_calls()
    {
        Assert.True(await Allowed(EntityAccessPolicies.Authenticated, EntityOperation.Delete, User()));
        Assert.False(await Allowed(EntityAccessPolicies.Authenticated, EntityOperation.Read, Anonymous));
        Assert.False(await Allowed(EntityAccessPolicies.Authenticated, EntityOperation.Read, null));
    }

    [Fact]
    public async Task ReadOnly_policy_allows_only_reads()
    {
        Assert.True(await Allowed(EntityAccessPolicies.ReadOnly, EntityOperation.Read, User()));
        Assert.False(await Allowed(EntityAccessPolicies.ReadOnly, EntityOperation.Update, User("admin")));
    }

    [Fact]
    public async Task Custom_policy_can_use_roles()
    {
        var policy = EntityAccessPolicies.From(r =>
            r.User?.Identity?.IsAuthenticated == true &&
            (r.Operation == EntityOperation.Read || r.User.IsInRole("admin")));

        Assert.True(await Allowed(policy, EntityOperation.Read, User()));
        Assert.False(await Allowed(policy, EntityOperation.Insert, User()));
        Assert.True(await Allowed(policy, EntityOperation.Insert, User("admin")));
    }

    [Fact]
    public void Handler_requires_a_policy()
        => Assert.Throws<ArgumentNullException>(() => new EntityHandler<TestClient>(() => throw new NotSupportedException(), null!));

    // ---------- Error codes ----------

    [Fact]
    public void Store_errors_keep_their_code_and_messages()
    {
        var result = DBResultMessage.FromException(new EntityStoreException(EntityErrorCode.Validation, "invalid", ["a", "b"]));

        var error = Assert.Throws<EntityStoreException>(() => result.GetDataOrThrow());
        Assert.Equal(EntityErrorCode.Validation, error.ErrorCode);
        Assert.Equal(new[] { "a", "b" }, error.Errors);
    }

    [Fact]
    public void Bad_requests_are_reported_as_such()
        => Assert.Equal(EntityErrorCode.InvalidRequest, DBResultMessage.FromException(new ArgumentException("filtre")).ErrorCode);

    [Fact]
    public void Unexpected_errors_are_unknown()
        => Assert.Equal(EntityErrorCode.Unknown, DBResultMessage.FromException(new InvalidOperationException("base")).ErrorCode);

    [Fact]
    public void Success_returns_the_data()
        => Assert.Equal(new byte[] { 1, 2 }, DBResultMessage.Success([1, 2]).GetDataOrThrow());

    [Fact]
    public void DBResult_survives_the_wire_form()
    {
        var original = DBResult.Failure(EntityErrorCode.Conflict, "changed in the meantime", ["v1"]);

        var back = DBResultMessage.From(original).ToDBResult();

        Assert.False(back.Successful);
        Assert.Equal(EntityErrorCode.Conflict, back.ErrorCode);
        Assert.Equal("changed in the meantime", back.Exception);
        Assert.Equal(new[] { "v1" }, back.Errors);
        Assert.True(DBResultMessage.From(DBResult.Success()).ToDBResult().Successful);
    }

    [Fact]
    public async Task Unknown_types_are_reported()
    {
        var dispatcher = new EntityRequestDispatcher(new EntityHandlerRegistry());
        var result = await dispatcher.GetAllAsync("Inexistant", EntityRequestContext.Anonymous);
        Assert.Equal(EntityErrorCode.UnknownType, result.ErrorCode);
    }

    [Fact]
    public async Task Batches_need_a_transactional_store()
    {
        var dispatcher = new EntityRequestDispatcher(new EntityHandlerRegistry());
        var result = await dispatcher.ExecuteBatchAsync([new BatchOperation("x", BatchOperationKind.Insert, [], null)], EntityRequestContext.Anonymous);
        Assert.Equal(EntityErrorCode.NotSupported, result.ErrorCode);
    }

    // ---------- Versions ----------

    public class WithIntVersion
    {
        public long Id { get; set; }
        [System.ComponentModel.DataAnnotations.ConcurrencyCheck] public int Version { get; set; }
    }

    public class WithTextVersion
    {
        public long Id { get; set; }
        [System.ComponentModel.DataAnnotations.ConcurrencyCheck] public string Version { get; set; } = "";
    }

    [Fact]
    public void Version_property_is_found()
    {
        Assert.Equal(nameof(TestClient.Version), ConcurrencyToken.Find(typeof(TestClient))?.Name);
        Assert.Null(ConcurrencyToken.Find(typeof(FiltersTests.Row)));
    }

    [Fact]
    public void Int_version_is_supported()
    {
        var entity = new WithIntVersion { Version = 3 };
        var property = ConcurrencyToken.Find(typeof(WithIntVersion))!;

        ConcurrencyToken.Set(entity, property, ConcurrencyToken.Get(entity, property) + 1);

        Assert.Equal(4, entity.Version);
    }

    [Fact]
    public void Non_numeric_version_is_rejected()
        => Assert.Throws<NotSupportedException>(() => ConcurrencyToken.Find(typeof(WithTextVersion)));
}
