using System.Security.Claims;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Infrastructure.Persistencia;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace IntegrationTests.Auditoria;

[Collection(AuditDatabaseCollection.Name)]
public sealed class AuditAuthorizationServiceTests
{
    private readonly AuditDatabaseFixture _database;
    private readonly AuditAuthorizationService _authorization;

    public AuditAuthorizationServiceTests(AuditDatabaseFixture database)
    {
        _database = database;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CorePostgres"] = database.ConnectionString
            })
            .Build();
        _authorization = new AuditAuthorizationService(configuration);
    }

    [Fact]
    public async Task ReportAccess_EnforcesOwnerResponsibleAndAdminRules()
    {
        Guid reportId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        Guid responsibleId = Guid.NewGuid();
        await _database.InsertReportAsync(reportId, ownerId, responsibleId);

        AuditUserContext owner = await _authorization.ResolveUserAsync(Principal(ownerId), CancellationToken.None);
        AuditUserContext otherCitizen = await _authorization.ResolveUserAsync(Principal(Guid.NewGuid()), CancellationToken.None);
        AuditUserContext assignedManager = await _authorization.ResolveUserAsync(Principal(responsibleId, "gestor"), CancellationToken.None);
        AuditUserContext otherManager = await _authorization.ResolveUserAsync(Principal(Guid.NewGuid(), "gestor"), CancellationToken.None);
        AuditUserContext admin = await _authorization.ResolveUserAsync(Principal(Guid.NewGuid(), "admin"), CancellationToken.None);

        Assert.Null(await _authorization.CheckReportAccessAsync(owner, reportId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound,
            await _authorization.CheckReportAccessAsync(otherCitizen, reportId, CancellationToken.None));
        Assert.Null(await _authorization.CheckReportAccessAsync(assignedManager, reportId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden,
            await _authorization.CheckReportAccessAsync(otherManager, reportId, CancellationToken.None));
        Assert.Null(await _authorization.CheckReportAccessAsync(admin, reportId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound,
            await _authorization.CheckReportAccessAsync(admin, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveUser_RejectsTokensWithoutSubject()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim("cognito:groups", "admin")], "test"));

        await Assert.ThrowsAsync<AuditAuthorizationException>(() =>
            _authorization.ResolveUserAsync(principal, CancellationToken.None));
    }

    private static ClaimsPrincipal Principal(Guid subject, string? role = null)
    {
        List<Claim> claims = [new Claim("sub", subject.ToString("D"))];
        if (role is not null)
            claims.Add(new Claim("cognito:groups", role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
