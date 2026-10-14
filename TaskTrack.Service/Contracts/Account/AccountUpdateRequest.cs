namespace TaskTrack.Service.Contracts;

public sealed class AccountUpdateRequest
{
    private string? _fullName;
    private short? _role;

    public string? FullName { get => _fullName; set { _fullName = value; FullNameSpecified = true; } }
    public short? Role { get => _role; set { _role = value; RoleSpecified = true; } }
    [System.Text.Json.Serialization.JsonIgnore] public bool FullNameSpecified { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool RoleSpecified { get; private set; }
}
