using System.ComponentModel.DataAnnotations;

namespace Project.Models;

/// <summary>Chính sách hệ số bồi thường dùng chung của thư viện (bản ghi duy nhất).</summary>
public sealed class CompensationPolicy
{
    public const int SingletonId = 1;
    public const decimal DefaultMultiplier = 1.0m;

    public int Id { get; set; } = SingletonId;

    /// <summary>Hệ số bồi thường chung (áp dụng khi không có hệ số riêng cho từng loại sự cố).</summary>
    [Range(0.01, 100.0, ErrorMessage = "Hệ số bồi thường chung phải lớn hơn 0 và không vượt quá 100.")]
    public decimal DefaultRate { get; set; } = DefaultMultiplier;

    /// <summary>Hệ số bồi thường khi mất sách (tùy chọn; nếu null sẽ dùng DefaultRate).</summary>
    [Range(0.01, 100.0, ErrorMessage = "Hệ số bồi thường khi mất phải lớn hơn 0 và không vượt quá 100.")]
    public decimal? LostRate { get; set; }

    /// <summary>Hệ số bồi thường khi hỏng nặng (tùy chọn; nếu null sẽ dùng DefaultRate).</summary>
    [Range(0.01, 100.0, ErrorMessage = "Hệ số bồi thường khi hỏng nặng phải lớn hơn 0 và không vượt quá 100.")]
    public decimal? DamagedRate { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(256)]
    public string? UpdatedBy { get; set; }

    public decimal LostRateOrDefault => LostRate.HasValue && LostRate.Value > 0 ? LostRate.Value : DefaultRate;
    public decimal DamagedRateOrDefault => DamagedRate.HasValue && DamagedRate.Value > 0 ? DamagedRate.Value : DefaultRate;

    public decimal GetRateFor(string? incidentType)
    {
        if (string.Equals(incidentType, BookIncidentType.Damaged, StringComparison.OrdinalIgnoreCase) && DamagedRate.HasValue && DamagedRate.Value > 0)
            return DamagedRate.Value;
        if (string.Equals(incidentType, BookIncidentType.Lost, StringComparison.OrdinalIgnoreCase) && LostRate.HasValue && LostRate.Value > 0)
            return LostRate.Value;
        return DefaultRate;
    }
}

public sealed class CompensationPolicyViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập hệ số bồi thường chung.")]
    [Range(0.01, 100.0, ErrorMessage = "Hệ số bồi thường chung phải lớn hơn 0 và không vượt quá 100.")]
    [Display(Name = "Hệ số bồi thường chung")]
    public decimal DefaultRate { get; set; } = CompensationPolicy.DefaultMultiplier;

    [Range(0.01, 100.0, ErrorMessage = "Hệ số bồi thường khi mất phải lớn hơn 0 và không vượt quá 100.")]
    [Display(Name = "Hệ số khi mất sách (tùy chọn)")]
    public decimal? LostRate { get; set; }

    [Range(0.01, 100.0, ErrorMessage = "Hệ số bồi thường khi hỏng nặng phải lớn hơn 0 và không vượt quá 100.")]
    [Display(Name = "Hệ số khi hỏng nặng (tùy chọn)")]
    public decimal? DamagedRate { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class UpdateCompensationPolicyRequest
{
    public decimal DefaultRate { get; set; }
    public decimal? LostRate { get; set; }
    public decimal? DamagedRate { get; set; }
}

/// <summary>Khoản bồi thường gắn với một bản ghi sự cố sách mất hoặc hỏng nặng.</summary>
public sealed class BookIncidentCompensation
{
    public long Id { get; set; }

    public long BookIncidentId { get; set; }
    public BookIncident? BookIncident { get; set; }

    /// <summary>Giá bìa của bản sao tại thời điểm ghi nhận sự cố (snapshot).</summary>
    public decimal? RecordedCoverPrice { get; set; }

    /// <summary>Hệ số bồi thường áp dụng tại thời điểm tính (snapshot).</summary>
    public decimal? AppliedRate { get; set; }

    /// <summary>Số tiền bồi thường ban đầu = RecordedCoverPrice * AppliedRate.</summary>
    public decimal InitialAmount { get; set; }

    /// <summary>Số tiền bồi thường hiện tại (sau điều chỉnh nếu có).</summary>
    public decimal CurrentAmount { get; set; }

    /// <summary>Cờ đánh dấu đã từng được điều chỉnh hay chưa.</summary>
    public bool IsAdjusted { get; set; }

    /// <summary>Lý do điều chỉnh gần nhất.</summary>
    [MaxLength(1000)]
    public string? AdjustmentReason { get; set; }

    /// <summary>Người điều chỉnh gần nhất.</summary>
    [MaxLength(256)]
    public string? AdjustedBy { get; set; }

    /// <summary>Thời điểm điều chỉnh gần nhất.</summary>
    public DateTime? AdjustedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<CompensationAdjustmentHistory> AdjustmentHistories { get; set; } = [];
}

/// <summary>Lịch sử các lần điều chỉnh số tiền bồi thường.</summary>
public sealed class CompensationAdjustmentHistory
{
    public long Id { get; set; }

    public long BookIncidentCompensationId { get; set; }
    public BookIncidentCompensation? Compensation { get; set; }

    public decimal FromAmount { get; set; }
    public decimal ToAmount { get; set; }

    [Required, MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string AdjustedBy { get; set; } = string.Empty;

    public DateTime AdjustedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AdjustCompensationRequest
{
    public long IncidentId { get; set; }
    public decimal NewAmount { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class AdjustCompensationViewModel
{
    [Range(1, long.MaxValue, ErrorMessage = "Vui lòng chọn sự cố hợp lệ.")]
    public long IncidentId { get; set; }

    [Display(Name = "Số tiền ban đầu")]
    public decimal InitialAmount { get; set; }

    [Display(Name = "Số tiền hiện tại")]
    public decimal CurrentAmount { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập số tiền điều chỉnh.")]
    [Range(0, 1000000000, ErrorMessage = "Số tiền điều chỉnh không được nhỏ hơn 0.")]
    [Display(Name = "Số tiền mới")]
    public decimal NewAmount { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập lý do điều chỉnh."), MaxLength(1000, ErrorMessage = "Lý do không được vượt quá 1000 ký tự.")]
    [Display(Name = "Lý do điều chỉnh")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class CompensationOutcome
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public BookIncidentCompensation? Compensation { get; init; }

    public static CompensationOutcome Success(BookIncidentCompensation compensation) =>
        new() { IsSuccess = true, Compensation = compensation };

    public static CompensationOutcome Failed(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}
