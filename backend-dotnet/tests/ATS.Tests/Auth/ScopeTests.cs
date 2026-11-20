using Ats.Application.Common;
using Ats.Application.Scope;
using Ats.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ats.Tests.Auth;

/// <summary>Reporting-scope rules (scope.py; the API-level checks follow with the endpoints in phase 4).</summary>
public sealed class ScopeTests : IDisposable
{
    private readonly AuthHost _host = new();

    public void Dispose() => _host.Dispose();

    private Org O => _host.Org;

    private async Task<T> With<T>(Func<ScopeService, Infrastructure.Persistence.AtsDbContext, Task<T>> run)
    {
        await using var db = _host.Db();
        return await run(new ScopeService(db), db);
    }

    private static async Task<User> Load(Infrastructure.Persistence.AtsDbContext db, User u) => await db.Users.SingleAsync(x => x.Id == u.Id);

    [Fact]
    public async Task Subtrees_include_indirect_reports()
    {
        (await With((s, _) => s.SubtreeIdsAsync(O.Vikram.Id))).Should().BeEquivalentTo([O.Priya.Id, O.Anil.Id, O.Rahul.Id, O.Sneha.Id, O.Arjun.Id]);
        (await With((s, _) => s.SubtreeIdsAsync(O.Priya.Id))).Should().BeEquivalentTo([O.Rahul.Id, O.Sneha.Id]);
        (await With((s, _) => s.SubtreeIdsAsync(O.Rahul.Id))).Should().BeEmpty();
        (await With((s, _) => s.ManagerChainIdsAsync(O.Rahul.Id))).Should().Equal(O.Priya.Id, O.Vikram.Id);
    }

    [Fact]
    public async Task A_reporting_cycle_terminates()
    {
        await using (var db = _host.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == O.Vikram.Id)).ManagerId = O.Rahul.Id; // Rahul -> Priya -> Vikram -> Rahul
            await db.SaveChangesAsync();
        }

        (await With((s, _) => s.SubtreeIdsAsync(O.Priya.Id))).Should().Contain([O.Rahul.Id, O.Vikram.Id, O.Priya.Id]);
        (await With((s, _) => s.ManagerChainIdsAsync(O.Rahul.Id))).Should().Equal(O.Priya.Id, O.Vikram.Id);
    }

    [Fact]
    public async Task Who_can_view_whom()
    {
        (await With(async (s, db) => await s.CanViewEmployeeAsync(await Load(db, O.Rahul), O.Rahul.Id))).Should().BeTrue();
        (await With(async (s, db) => await s.CanViewEmployeeAsync(await Load(db, O.Rahul), O.Sneha.Id))).Should().BeFalse();
        (await With(async (s, db) => await s.CanViewEmployeeAsync(await Load(db, O.Priya), O.Rahul.Id))).Should().BeTrue();
        (await With(async (s, db) => await s.CanViewEmployeeAsync(await Load(db, O.Priya), O.Arjun.Id))).Should().BeFalse();
        (await With(async (s, db) => await s.CanViewEmployeeAsync(await Load(db, O.Vikram), O.Arjun.Id))).Should().BeTrue();
        (await With(async (s, db) => await s.CanViewEmployeeAsync(await Load(db, O.Admin), O.Arjun.Id))).Should().BeTrue();

        var outOfScope = () => With(async (s, db) => await s.GetViewableEmployeeAsync(await Load(db, O.Priya), O.Arjun.Id));
        (await outOfScope.Should().ThrowAsync<NotFoundException>()).WithMessage("Employee not found");
        var missing = () => With(async (s, db) => await s.GetViewableEmployeeAsync(await Load(db, O.Admin), 99999));
        (await missing.Should().ThrowAsync<NotFoundException>()).WithMessage("Employee not found"); // same answer: ids cannot be probed
    }

    [Fact]
    public async Task Resolve_scope_for_each_role()
    {
        var employee = () => With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Rahul)));
        (await employee.Should().ThrowAsync<ForbiddenException>()).WithMessage("Team data is only available to managers and administrators");

        var priya = await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Priya)));
        priya.Select(u => u.FullName).Should().Equal("Rahul Reddy", "Sneha Rao"); // ordered by name
        priya[0].Department!.Name.Should().Be("AI Engineering");
        priya[0].Manager!.FullName.Should().Be("Priya Sharma");

        var team = await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Vikram), managerId: O.Priya.Id));
        team.Select(u => u.Id).Should().BeEquivalentTo([O.Rahul.Id, O.Sneha.Id]);

        var unrelated = () => With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Priya), managerId: O.Anil.Id));
        (await unrelated.Should().ThrowAsync<NotFoundException>()).WithMessage("Manager not found");

        (await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin)))).Should().HaveCount(7);
        (await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin), departmentId: O.Ai.Id)))
            .Select(u => u.Id).Should().BeEquivalentTo([O.Priya.Id, O.Rahul.Id, O.Sneha.Id]);
        var unknownManager = () => With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin), managerId: 99999));
        await unknownManager.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Resolve_scope_filters()
    {
        await using (var db = _host.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == O.Sneha.Id)).IsActive = false;
            (await db.Users.SingleAsync(u => u.Id == O.Arjun.Id)).AttendanceTracked = false;
            await db.SaveChangesAsync();
        }

        (await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin)))).Should().HaveCount(5);
        (await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin), includeInactive: true))).Should().HaveCount(6);
        (await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin), trackedOnly: false))).Should().HaveCount(6);
        (await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin), search: "  KUMAR ", trackedOnly: false)))
            .Select(u => u.FullName).Should().Equal("Anil Kumar", "Arjun Kumar");
        (await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin), search: "e00")))
            .Should().HaveCount(5); // employee codes E001..E007
        (await With(async (s, db) => await s.ResolveScopeAsync(await Load(db, O.Admin), limit: 2))).Select(u => u.FullName)
            .Should().Equal("Admin User", "Anil Kumar");
    }
}
