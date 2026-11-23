using Ats.Application.Data;
using Ats.Domain.Entities;
using Ats.Domain.Rules;

namespace Ats.Application.Attendance;

/// <summary>The organisation's single policy row (<c>app/services/policy.py</c>).</summary>
public static class Policies
{
    /// <summary>
    /// The policy row; when it does not exist yet a default one is added to the unit of work, as Python's get_policy
    /// adds and flushes it. It is saved only if the request saves (GET /policy does: parity note P3).
    /// </summary>
    public static async Task<AttendancePolicy> GetAsync(IAtsDb db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var policy = await db.AttendancePolicies.FindAsync([1], ct).ConfigureAwait(false);
        if (policy is null)
        {
            policy = new AttendancePolicy { Id = 1 };
            db.AttendancePolicies.Add(policy);
        }

        return policy;
    }

    public static PolicyData Data(AttendancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new PolicyData(policy.MinPresentMinutes, policy.LateGraceMinutes);
    }

    public static WfoPolicy Wfo(AttendancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new WfoPolicy(policy.MinWfoDaysPerWeek, policy.MinWfoDaysPerMonth, policy.MinOfficeMinutesForWfoDay);
    }
}
