using System;
using System.Collections.Generic;

namespace TaskTrack.Repo.Models;

public partial class Department
{
    public int DepartmentId { get; set; }

    public string DepartmentName { get; set; } = null!;

    public string DepartmentDescription { get; set; } = null!;

    public bool IsActive { get; set; }

    public virtual ICollection<Project> Projects { get; set; } = new List<Project>();
}
