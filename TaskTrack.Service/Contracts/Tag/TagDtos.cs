using System.ComponentModel.DataAnnotations;

namespace TaskTrack.Service.Contracts;

public abstract class TagWriteRequest
{
    [Required, StringLength(50)] public string TagName { get; set; } = string.Empty;
    private string? color;
    [StringLength(7), RegularExpression("^#[0-9a-fA-F]{6}$")]
    public string? Color { get => color; set => color = string.IsNullOrEmpty(value) ? null : value; }
}
public sealed class TagCreateRequest : TagWriteRequest { }
public sealed class TagUpdateRequest : TagWriteRequest { }
public record TagListItem
{
    public int TagId { get; init; }
    public string TagName { get; init; } = string.Empty;
    public string? Color { get; init; }
}
public sealed record TagDetail : TagListItem;
