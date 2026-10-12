using System.ComponentModel.DataAnnotations;

namespace Project.Models;

public sealed class CreateAuthorViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ tên tác giả.")]
    [Display(Name = "Họ tên tác giả")]
    [MaxLength(150, ErrorMessage = "Họ tên tác giả không được vượt quá 150 ký tự.")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Ghi chú")]
    [MaxLength(500, ErrorMessage = "Ghi chú không được vượt quá 500 ký tự.")]
    public string? Note { get; set; }
}

public sealed class EditAuthorViewModel
{
    [Required]
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập họ tên tác giả.")]
    [Display(Name = "Họ tên tác giả")]
    [MaxLength(150, ErrorMessage = "Họ tên tác giả không được vượt quá 150 ký tự.")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Ghi chú")]
    [MaxLength(500, ErrorMessage = "Ghi chú không được vượt quá 500 ký tự.")]
    public string? Note { get; set; }
}

public sealed class AuthorIndexViewModel
{
    public CreateAuthorViewModel NewAuthor { get; set; } = new();
    public EditAuthorViewModel? EditingAuthor { get; set; }
    public IReadOnlyList<Author> Authors { get; set; } = [];
    public bool CanManage { get; set; }
}

public sealed class CreateAuthorDto
{
    [Required(ErrorMessage = "Vui lòng nhập họ tên tác giả.")]
    [MaxLength(150, ErrorMessage = "Họ tên tác giả không được vượt quá 150 ký tự.")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Ghi chú không được vượt quá 500 ký tự.")]
    public string? Note { get; set; }
}

public sealed class UpdateAuthorDto
{
    [Required(ErrorMessage = "Vui lòng nhập họ tên tác giả.")]
    [MaxLength(150, ErrorMessage = "Họ tên tác giả không được vượt quá 150 ký tự.")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Ghi chú không được vượt quá 500 ký tự.")]
    public string? Note { get; set; }
}

public sealed class UpdateAuthorStatusDto
{
    [MaxLength(50)]
    public string? Status { get; set; }
}
