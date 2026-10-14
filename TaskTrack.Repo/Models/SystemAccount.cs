namespace TaskTrack.Repo.Models;

public partial class SystemAccount
{
    public int AccountId { get; set; }
    public string FullName { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public short Role { get; set; }
    public DateTime CreatedDate { get; set; }
    public virtual ICollection<Task> CreatedTasks { get; set; } = new List<Task>();
}
