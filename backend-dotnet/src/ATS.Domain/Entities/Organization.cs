namespace Ats.Domain.Entities;

/// <summary>created_at / updated_at maintained on save (SQLAlchemy TimestampMixin).</summary>
public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>created_at set on insert only.</summary>
public interface ICreatedAt
{
    DateTimeOffset CreatedAt { get; set; }
}

/// <summary>departments: names are unique organisation-wide ignoring case (expression index on lower(name)).</summary>
public class Department : ITimestamped
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public int? DepartmentHeadId { get; set; }

    public User? Head { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>users: employees, managers and the HR admin. Email is stored lower-case.</summary>
public class User : ITimestamped
{
    public int Id { get; set; }

    public string EmployeeCode { get; set; } = "";

    public string FullName { get; set; } = "";

    public string Email { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public Role Role { get; set; }

    public string? JobTitle { get; set; }

    public int? DepartmentId { get; set; }

    public Department? Department { get; set; }

    public int? ManagerId { get; set; }

    public User? Manager { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>False for service / administrative accounts kept out of attendance statistics.</summary>
    public bool AttendanceTracked { get; set; } = true;

    public DateOnly DateJoined { get; set; }

    /// <summary>Entra object id (linked at the first Microsoft sign-in).</summary>
    public string? GraphUserId { get; set; }

    /// <summary>802.1X username or MAC address (normalised lower-case) for office Wi-Fi.</summary>
    public string? WifiIdentity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
